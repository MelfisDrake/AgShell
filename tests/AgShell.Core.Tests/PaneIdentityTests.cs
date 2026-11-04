using AgShell.Protocol;

namespace AgShell.Core.Tests
{
    public class PaneIdentityTests
    {
        [Fact]
        public void NewSessionCarriesItsMainPaneIdIntoTheFirstSave()
        {
            AppModel model = new AppModel();
            Workspace workspace = model.AddWorkspace("w");
            List<StateSnapshot> saves = new List<StateSnapshot>();
            model.StructureChanged += () => saves.Add(model.ToSnapshot());

            Session session = model.AddSession(workspace.Id, "p", "C:\\", name: "fresh");

            SessionSnapshot saved = saves.Last().Workspaces.Single(w => w.Id == workspace.Id).Sessions.Single(s => s.Id == session.Id);
            Assert.True(LiveReap.IsDaemonName(saved.PaneIds!["left"]));
            Assert.Equal(saved.PaneIds["left"], model.PaneIdFor(session.Id, PaneRole.Main));
        }

        [Fact]
        public void NewSplitCarriesItsIdIntoTheSaveAndKeepsItWhenHiddenAndShown()
        {
            AppModel model = new AppModel();
            Workspace workspace = model.AddWorkspace("w");
            Session session = model.AddSession(workspace.Id, "p", "C:\\");
            StateSnapshot? last = null;
            model.StructureChanged += () => last = model.ToSnapshot();

            model.SetSplit(session.Id, true);
            string split = last!.Workspaces.SelectMany(w => w.Sessions).Single(s => s.Id == session.Id).PaneIds!["right"];
            model.SetSplit(session.Id, false);
            model.SetSplit(session.Id, true);

            Assert.True(LiveReap.IsDaemonName(split));
            Assert.Equal(split, model.PaneIdFor(session.Id, PaneRole.Split));
        }

        [Fact]
        public void PromotedSplitKeepsItsIdAsTheMainPane()
        {
            AppModel model = new AppModel();
            Workspace workspace = model.AddWorkspace("w");
            Session session = model.AddSession(workspace.Id, "p", "C:\\");
            model.SetSplit(session.Id, true);
            string split = model.PaneIdFor(session.Id, PaneRole.Split);

            model.PromoteSplit(session.Id);

            Assert.Equal(split, session.PaneIds[PaneRole.Main]);
            Assert.False(session.PaneIds.ContainsKey(PaneRole.Split));
        }

        [Fact]
        public void RestoredSessionWithoutIdsGetsNewOnesAndKeepsSavedOnes()
        {
            string kept = Guid.NewGuid().Wire();
            Guid bare = Guid.NewGuid();
            Guid partial = Guid.NewGuid();
            StateSnapshot snapshot = new StateSnapshot
            {
                Workspaces = new List<WorkspaceSnapshot>
                {
                    new WorkspaceSnapshot
                    {
                        Id = Guid.NewGuid(),
                        Name = "w",
                        Sessions = new List<SessionSnapshot>
                        {
                            new SessionSnapshot { Id = bare, Cwd = "C:\\", HasSplit = true },
                            new SessionSnapshot { Id = partial, Cwd = "C:\\", PaneIds = new Dictionary<string, string> { ["left"] = kept } },
                        },
                    },
                },
            };

            AppModel model = AppModel.FromSnapshot(snapshot);

            Session restored = model.FindSession(bare)!;
            Assert.True(LiveReap.IsDaemonName(restored.PaneIds[PaneRole.Main]));
            Assert.True(LiveReap.IsDaemonName(restored.PaneIds[PaneRole.Split]));
            Assert.Equal(kept, model.FindSession(partial)!.PaneIds[PaneRole.Main]);
        }

        [Fact]
        public void InventoryMintsOnlyMissingIdsAndReportsTheChange()
        {
            string left = Guid.NewGuid().Wire();
            string right = Guid.NewGuid().Wire();
            WorkspaceSnapshot workspace = new WorkspaceSnapshot
            {
                Id = Guid.NewGuid(),
                Sessions = new List<SessionSnapshot>
                {
                    new SessionSnapshot { Id = Guid.NewGuid(), HasSplit = true, PaneIds = new Dictionary<string, string> { ["left"] = left, ["right"] = right } },
                    new SessionSnapshot { Id = Guid.NewGuid(), HasSplit = true, PaneIds = new Dictionary<string, string> { ["left"] = Guid.NewGuid().Wire() } },
                    new SessionSnapshot { Id = Guid.NewGuid() },
                },
            };
            StateSnapshot snapshot = new StateSnapshot { Workspaces = new List<WorkspaceSnapshot> { workspace } };

            PaneIdentityInventory.Upgrade upgrade = PaneIdentityInventory.Run(snapshot);

            Assert.True(upgrade.Changed);
            Assert.Equal(5, upgrade.Identities.Count);
            Assert.Equal(new[] { left, right }, workspace.Sessions[0].PaneIds!.Values.ToArray());
            Assert.True(LiveReap.IsDaemonName(workspace.Sessions[1].PaneIds!["right"]));
            Assert.True(LiveReap.IsDaemonName(workspace.Sessions[2].PaneIds!["left"]));
            Assert.False(workspace.Sessions[2].PaneIds!.ContainsKey("right"));
            Assert.False(PaneIdentityInventory.Run(snapshot).Changed);
        }

        [Theory]
        [InlineData(null, false)]
        [InlineData("", false)]
        [InlineData(null, true)]
        [InlineData("", true)]
        public void AnUnsetPaneIdGetsAnIdentityWithoutChangingItsNeighbor(string? unset, bool inventoryFirst)
        {
            Guid id = Guid.NewGuid();
            string kept = Guid.NewGuid().Wire();
            StateSnapshot snapshot = new StateSnapshot
            {
                Workspaces = new List<WorkspaceSnapshot>
                {
                    new WorkspaceSnapshot
                    {
                        Id = Guid.NewGuid(),
                        Sessions = new List<SessionSnapshot>
                        {
                            new SessionSnapshot { Id = id, HasSplit = true, PaneIds = new Dictionary<string, string> { ["left"] = unset!, ["right"] = kept } },
                        },
                    },
                },
            };

            if (inventoryFirst)
            {
                Assert.True(PaneIdentityInventory.Run(snapshot).Changed);
            }

            Session restored = AppModel.FromSnapshot(snapshot).FindSession(id)!;
            Assert.True(Guid.TryParse(restored.PaneIds[PaneRole.Main], out _));
            Assert.Equal(kept, restored.PaneIds[PaneRole.Split]);
            Assert.Equal(!inventoryFirst, PaneIdentityInventory.Run(snapshot).Changed);
            Assert.True(Guid.TryParse(snapshot.Workspaces[0].Sessions[0].PaneIds!["left"], out _));
            Assert.Equal(kept, snapshot.Workspaces[0].Sessions[0].PaneIds!["right"]);
        }
    }
}
