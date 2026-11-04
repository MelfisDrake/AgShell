namespace AgShell.Core.Tests
{
    public class AppModelTests
    {
        [Fact]
        public void AddSessionSelectsItAndExpandsWorkspace()
        {
            AppModel model = new AppModel();
            Workspace workspace = model.AddWorkspace("work");
            model.SetWorkspaceCollapsed(workspace.Id, true);

            Session session = model.AddSession(workspace.Id, "cmd", @"C:\src");

            Assert.Equal(session.Id, model.SelectedSessionId);
            // As agterm's reveal: open on screen, folded as saved.
            Assert.True(workspace.Collapsed);
            Assert.False(model.ShownCollapsed(workspace));
            Assert.Same(workspace, model.CurrentWorkspace);
        }

        [Fact]
        public void BlankNamesAreHandled()
        {
            AppModel model = new AppModel();
            Workspace workspace = model.AddWorkspace("  ");
            Session session = model.AddSession(workspace.Id, "cmd", @"C:\src\agshell");

            model.RenameWorkspace(workspace.Id, " ");
            model.RenameSession(session.Id, "agent");
            model.RenameSession(session.Id, "   ");

            Assert.Equal("workspace 1", workspace.Name);
            Assert.Null(session.CustomName);
            Assert.Equal("agshell", session.DisplayName);
        }

        [Fact]
        public void MoveSessionAcrossWorkspacesKeepsRequestedIndex()
        {
            AppModel model = new AppModel();
            Workspace first = model.AddWorkspace("first");
            Workspace second = model.AddWorkspace("second");
            Session a = model.AddSession(first.Id, "cmd", null);
            Session b = model.AddSession(second.Id, "cmd", null);
            Session c = model.AddSession(second.Id, "cmd", null);

            model.MoveSession(a.Id, second.Id, 1);

            Assert.Empty(first.Sessions);
            Assert.Equal(new[] { b.Id, a.Id, c.Id }, second.Sessions.Select(session => session.Id));
        }

        [Fact]
        public void MoveSessionWithinWorkspaceClampsIndex()
        {
            AppModel model = new AppModel();
            Workspace workspace = model.AddWorkspace("w");
            Session a = model.AddSession(workspace.Id, "cmd", null);
            Session b = model.AddSession(workspace.Id, "cmd", null);

            model.MoveSession(a.Id, workspace.Id, 99);

            Assert.Equal(new[] { b.Id, a.Id }, workspace.Sessions.Select(session => session.Id));
        }

        [Fact]
        public void RemovingSelectedSessionReturnsToMostRecentInSameWorkspace()
        {
            AppModel model = new AppModel();
            Workspace work = model.AddWorkspace("work");
            Workspace other = model.AddWorkspace("other");
            Session a = model.AddSession(work.Id, "cmd", null);
            Session b = model.AddSession(work.Id, "cmd", null);
            Session c = model.AddSession(work.Id, "cmd", null);
            Session elsewhere = model.AddSession(other.Id, "cmd", null);
            model.SelectSession(a.Id);
            model.SelectSession(elsewhere.Id);
            model.SelectSession(c.Id);

            model.RemoveSession(c.Id);
            Assert.Equal(a.Id, model.SelectedSessionId);

            model.RemoveSession(a.Id);
            Assert.Equal(b.Id, model.SelectedSessionId);

            model.RemoveSession(b.Id);
            Assert.Equal(elsewhere.Id, model.SelectedSessionId);

            model.RemoveSession(elsewhere.Id);
            Assert.Null(model.SelectedSessionId);
        }

        [Fact]
        public void RemovingSelectedSessionWithoutHistoryFallsBackToNeighbour()
        {
            AppModel model = AppModel.FromSnapshot(new StateSnapshot
            {
                Workspaces = new List<WorkspaceSnapshot>
                {
                    new WorkspaceSnapshot
                    {
                        Id = Guid.NewGuid(),
                        Name = "w",
                        Sessions = new List<SessionSnapshot>
                        {
                            new SessionSnapshot { Id = Guid.NewGuid(), ProfileId = "cmd" },
                            new SessionSnapshot { Id = Guid.NewGuid(), ProfileId = "cmd" },
                            new SessionSnapshot { Id = Guid.NewGuid(), ProfileId = "cmd" },
                        },
                    },
                },
            });
            IReadOnlyList<Session> sessions = model.Workspaces[0].Sessions;
            Guid second = sessions[1].Id;
            Guid third = sessions[2].Id;
            model.SelectSession(second);

            model.RemoveSession(second);

            Assert.Equal(third, model.SelectedSessionId);
        }

        [Fact]
        public void NewWorkspaceReceivesNewSessionsUntilSelectionMoves()
        {
            AppModel model = new AppModel();
            Workspace first = model.AddWorkspace();
            Session a = model.AddSession(first.Id, "cmd", null);

            Workspace second = model.AddWorkspace();
            Assert.Equal("workspace 2", second.Name);
            Assert.Empty(second.Sessions);
            Assert.Equal(a.Id, model.SelectedSessionId);
            Assert.Same(second, model.CurrentWorkspace);

            model.SelectSession(a.Id);
            Assert.Same(second, model.CurrentWorkspace);

            Session b = model.AddSession(first.Id, "cmd", null);
            Assert.Same(first, model.CurrentWorkspace);
            model.SelectSession(a.Id);
            Assert.Same(first, model.CurrentWorkspace);
            Assert.Contains(b, first.Sessions);
        }

        [Fact]
        public void LastWorkspaceIsKept()
        {
            AppModel model = new AppModel();
            Workspace only = model.AddWorkspace("only");
            model.AddSession(only.Id, "cmd", null);

            Assert.False(model.CanRemoveWorkspace);
            Assert.Empty(model.RemoveWorkspace(only.Id));
            Assert.Single(model.Workspaces);
            Assert.Single(model.AllSessions);
        }

        [Fact]
        public void DuplicateGoesRightAfterSourceWithItsShellAndDirectory()
        {
            AppModel model = new AppModel();
            Workspace workspace = model.AddWorkspace("w");
            Session a = model.AddSession(workspace.Id, "wsl:Ubuntu", "/home");
            Session b = model.AddSession(workspace.Id, "cmd", null);
            model.RenameSession(a.Id, "agent");
            model.UpdateCwd(a.Id, "/tmp");

            Session copy = model.DuplicateSession(a.Id);

            Assert.Equal(new[] { a.Id, copy.Id, b.Id }, workspace.Sessions.Select(session => session.Id));
            Assert.Equal("wsl:Ubuntu", copy.ProfileId);
            Assert.Equal("/tmp", copy.Cwd);
            Assert.Null(copy.CustomName);
            Assert.Equal(copy.Id, model.SelectedSessionId);
        }

        [Fact]
        public void SelectionIsNotStructural()
        {
            AppModel model = new AppModel();
            Workspace workspace = model.AddWorkspace("w");
            Session a = model.AddSession(workspace.Id, "cmd", null);
            model.AddSession(workspace.Id, "cmd", null);
            int structural = 0;
            int selection = 0;
            model.StructureChanged += () => structural++;
            model.SelectionChanged += () => selection++;

            model.SelectSession(a.Id);

            Assert.Equal(0, structural);
            Assert.Equal(1, selection);
        }

        [Fact]
        public void RemoveWorkspaceReturnsItsSessionsAndMovesSelection()
        {
            AppModel model = new AppModel();
            Workspace first = model.AddWorkspace("first");
            Workspace second = model.AddWorkspace("second");
            Session keep = model.AddSession(first.Id, "cmd", null);
            Session gone = model.AddSession(second.Id, "cmd", null);

            IReadOnlyList<Guid> removed = model.RemoveWorkspace(second.Id);

            Assert.Equal(new[] { gone.Id }, removed);
            Assert.Equal(keep.Id, model.SelectedSessionId);
            Assert.Single(model.Workspaces);
        }

        [Fact]
        public void SelectRelativeWrapsAroundAllWorkspaces()
        {
            AppModel model = new AppModel();
            Workspace first = model.AddWorkspace("first");
            Workspace second = model.AddWorkspace("second");
            Session a = model.AddSession(first.Id, "cmd", null);
            Session b = model.AddSession(second.Id, "cmd", null);
            model.SetWorkspaceCollapsed(first.Id, true);

            model.SelectRelative(1);
            Assert.Equal(a.Id, model.SelectedSessionId);
            Assert.False(model.ShownCollapsed(first));

            model.SelectRelative(-1);
            Assert.Equal(b.Id, model.SelectedSessionId);
        }

        [Fact]
        public void CwdUpdateIsNotStructural()
        {
            AppModel model = new AppModel();
            Workspace workspace = model.AddWorkspace("w");
            Session session = model.AddSession(workspace.Id, "cmd", @"C:\");
            int structural = 0;
            int any = 0;
            model.StructureChanged += () => structural++;
            model.Changed += () => any++;

            model.UpdateCwd(session.Id, @"C:\src");
            model.UpdateCwd(session.Id, @"C:\src");

            Assert.Equal(0, structural);
            Assert.Equal(1, any);
            Assert.Equal("src", session.DisplayName);
        }

        [Fact]
        public void TitleNamesTheSessionUntilTheShellTakesItBack()
        {
            AppModel model = new AppModel();
            Workspace workspace = model.AddWorkspace("w");
            Session session = model.AddSession(workspace.Id, "wsl", "/home/developer/src");
            int structural = 0;
            int any = 0;
            int labels = 0;
            model.StructureChanged += () => structural++;
            model.Changed += () => any++;
            model.LabelChanged += changed => labels++;

            model.UpdateTitle(session.Id, "✳ Fix the icon");
            model.UpdateTitle(session.Id, "✳ Fix the icon");
            Assert.Equal("Fix the icon", session.DisplayName);

            model.UpdateTitle(session.Id, "drake@host: ~/src");
            Assert.Equal("src", session.DisplayName);
            Assert.Equal(0, structural);
            Assert.Equal(0, any);
            Assert.Equal(2, labels);

            model.UpdateTitle(session.Id, "✳ Fix the icon");
            model.RenameSession(session.Id, "agent");
            Assert.Equal("agent", session.DisplayName);
        }

        [Fact]
        public void TheCommandFollowsItsPaneThroughSwapAndPromotion()
        {
            AppModel model = new AppModel();
            Workspace workspace = model.AddWorkspace("w");
            Session session = model.AddSession(workspace.Id, "cmd", @"C:\src");
            model.SetCommand(session.Id, "htop", wait: true);
            model.SetSplit(session.Id, true);

            model.SwapPanes(session.Id);
            Assert.Null(session.Command);
            Assert.Equal("htop", session.SplitCommand);
            Assert.True(session.SplitCommandWait);

            // The left shell ended: the right pane, with the command, is the session's only one now.
            model.PromoteSplit(session.Id);
            Assert.Equal("htop", session.Command);
            Assert.True(session.CommandWait);
            Assert.Null(session.SplitCommand);

            // The command's own pane ended with a plain shell beside it: that shell carries no command.
            model.SetSplit(session.Id, true);
            model.PromoteSplit(session.Id);
            Assert.Null(session.Command);
            Assert.False(session.CommandWait);
        }

        [Fact]
        public void ControlCharactersNeverReachNamesOrDirectories()
        {
            AppModel model = new AppModel();
            Workspace workspace = model.AddWorkspace("a\nb");
            Session session = model.AddSession(workspace.Id, "cmd", "C:\\x\ny", name: "n\u0007ame");
            Assert.Equal("ab", workspace.Name);
            Assert.Equal("C:\\xy", session.Cwd);
            Assert.Equal("name", session.CustomName);

            model.UpdateCwd(session.Id, "C:\\a\r\nb");
            Assert.Equal("C:\\ab", session.Cwd);
            model.RenameSession(session.Id, "\n\t");
            Assert.Null(session.CustomName);
            model.RenameWorkspace(workspace.Id, "\u001b");
            Assert.Equal("ab", workspace.Name);
        }

        [Fact]
        public void AnUnstartedPaneOverlayGoesWhenItsPaneIsNoLongerLaidOut()
        {
            AppModel model = new AppModel();
            Workspace workspace = model.AddWorkspace("w");
            Session session = model.AddSession(workspace.Id, "cmd", @"C:\src");
            model.SetSplit(session.Id, true);
            model.OpenOverlay(session.Id, new SessionOverlay { Id = "left", Command = "revdiff" }, PaneRole.OverlayLeft);
            model.OpenOverlay(session.Id, new SessionOverlay { Id = "right", Command = "htop" }, PaneRole.OverlayRight);

            // Its pane laid out, or its program started: kept.
            Assert.False(model.DropUnrealizedPaneOverlays(session.Id, _ => false));
            model.FocusPane(session.Id, PaneRole.Main);
            model.SetSplit(session.Id, false);
            Assert.False(model.DropUnrealizedPaneOverlays(session.Id, slot => slot == PaneRole.OverlayRight));
            Assert.True(session.Overlays.ContainsKey(PaneRole.OverlayRight));

            Assert.True(model.DropUnrealizedPaneOverlays(session.Id, _ => false));
            Assert.False(session.Overlays.ContainsKey(PaneRole.OverlayRight));
            Assert.Null(session.LastOverlay(PaneRole.OverlayRight));
            Assert.True(session.Overlays.ContainsKey(PaneRole.OverlayLeft));
        }

        [Fact]
        public void AFocusFlipOnAHiddenSplitDropsTheOtherPanesUnstartedOverlay()
        {
            AppModel model = new AppModel();
            Workspace workspace = model.AddWorkspace("w");
            Session session = model.AddSession(workspace.Id, "cmd", @"C:\src");
            model.SetSplit(session.Id, true);
            model.FocusPane(session.Id, PaneRole.Split);
            model.SetSplit(session.Id, false);
            model.OpenOverlay(session.Id, new SessionOverlay { Id = "right", Command = "htop" }, PaneRole.OverlayRight);
            model.OpenOverlay(session.Id, new SessionOverlay { Id = "page", Page = new PageSpec { Source = "https://example.com" } }, PaneRole.OverlayLeft);
            Assert.False(model.DropUnrealizedPaneOverlays(session.Id, _ => false));

            model.FocusPane(session.Id, PaneRole.Main);
            Assert.True(model.DropUnrealizedPaneOverlays(session.Id, _ => false));
            Assert.False(session.Overlays.ContainsKey(PaneRole.OverlayRight));
            Assert.True(session.Overlays.ContainsKey(PaneRole.OverlayLeft));
        }

        [Fact]
        public void TheZoomedPaneOverlayIsKeptUntilTheZoomLeaves()
        {
            AppModel model = new AppModel();
            Workspace workspace = model.AddWorkspace("w");
            Session session = model.AddSession(workspace.Id, "cmd", @"C:\src");
            model.SetSplit(session.Id, true);
            model.OpenOverlay(session.Id, new SessionOverlay { Id = "right", Command = "htop" }, PaneRole.OverlayRight);
            model.ZoomSurface(session.Id, PaneRole.OverlayRight, true);
            model.FocusPane(session.Id, PaneRole.Main);
            model.SetSplit(session.Id, false);
            Assert.False(model.DropUnrealizedPaneOverlays(session.Id, _ => false));

            model.ZoomSurface(session.Id, PaneRole.OverlayRight, false);
            Assert.True(model.DropUnrealizedPaneOverlays(session.Id, _ => false));

            // The pane zoomed instead of its overlay does not keep the overlay.
            model.SetSplit(session.Id, true);
            model.OpenOverlay(session.Id, new SessionOverlay { Id = "again", Command = "htop" }, PaneRole.OverlayRight);
            model.ZoomSurface(session.Id, PaneRole.Split, true);
            model.FocusPane(session.Id, PaneRole.Main);
            model.SetSplit(session.Id, false);
            Assert.True(model.DropUnrealizedPaneOverlays(session.Id, _ => false));
        }

        [Fact]
        public void AnOverlayReportsNoDirectory()
        {
            AppModel model = new AppModel();
            Workspace workspace = model.AddWorkspace("w");
            Session session = model.AddSession(workspace.Id, "cmd", @"C:\src");
            model.SetSplit(session.Id, true);

            model.UpdateCwd(session.Id, @"C:\overlay", PaneRole.OverlayRight);
            model.UpdateCwd(session.Id, @"C:\overlay", PaneRole.Overlay);

            Assert.Equal(@"C:\src", session.Cwd);
            Assert.NotEqual(@"C:\overlay", session.SplitCwd);
        }

        [Fact]
        public void TitlesFollowTheirPanes()
        {
            AppModel model = new AppModel();
            Workspace workspace = model.AddWorkspace("w");
            Session session = model.AddSession(workspace.Id, "wsl", "/home/developer/src");
            model.SetSplit(session.Id, true);
            model.UpdateTitle(session.Id, "main", PaneRole.Main);
            model.UpdateTitle(session.Id, "split", PaneRole.Split);
            model.UpdateTitle(session.Id, "scratch", PaneRole.Scratch);
            Assert.Equal("split", session.DisplayName);

            model.SwapPanes(session.Id);
            Assert.Equal("split", session.DisplayName);
            Assert.Equal("split", session.Title);
            Assert.Equal("main", session.SplitTitle);

            model.PromoteSplit(session.Id);
            Assert.Equal("main", session.Title);
            Assert.Null(session.SplitTitle);
            Assert.Equal("main", session.DisplayName);

            model.SetSplit(session.Id, true);
            model.UpdateTitle(session.Id, "split", PaneRole.Split);
            model.CloseSplit(session.Id);
            Assert.Null(session.SplitTitle);
            Assert.Equal("main", session.DisplayName);
        }

        [Fact]
        public void SnapshotRoundTrips()
        {
            AppModel model = new AppModel();
            Workspace workspace = model.AddWorkspace("work");
            Session session = model.AddSession(workspace.Id, "wsl:Ubuntu", "/home/developer");
            model.RenameSession(session.Id, "agent");
            model.AddWorkspace("empty");

            AppModel restored = AppModel.FromSnapshot(model.ToSnapshot());

            Assert.Equal(new[] { "work", "empty" }, restored.Workspaces.Select(w => w.Name));
            Session restoredSession = Assert.Single(restored.AllSessions);
            Assert.Equal(session.Id, restoredSession.Id);
            Assert.Equal("wsl:Ubuntu", restoredSession.ProfileId);
            Assert.Equal("/home/developer", restoredSession.Cwd);
            Assert.Equal("agent", restoredSession.DisplayName);
            Assert.Equal(session.Id, restored.SelectedSessionId);
        }

        [Fact]
        public void RecencySurvivesSnapshot()
        {
            AppModel model = new AppModel();
            Workspace workspace = model.AddWorkspace("w");
            Session a = model.AddSession(workspace.Id, "cmd", null);
            Session b = model.AddSession(workspace.Id, "cmd", null);
            Session c = model.AddSession(workspace.Id, "cmd", null);
            model.SelectSession(a.Id);

            AppModel restored = AppModel.FromSnapshot(model.ToSnapshot());

            Assert.Equal(new[] { a.Id, c.Id, b.Id }, restored.RecentSessions);
        }

        [Fact]
        public void SnapshotWithDuplicatesAndStaleSelectionIsRepaired()
        {
            Guid duplicate = Guid.NewGuid();
            StateSnapshot snapshot = new StateSnapshot
            {
                SelectedSessionId = Guid.NewGuid(),
                Workspaces = new List<WorkspaceSnapshot>
                {
                    new WorkspaceSnapshot
                    {
                        Id = Guid.NewGuid(),
                        Name = "w",
                        Sessions = new List<SessionSnapshot>
                        {
                            new SessionSnapshot { Id = duplicate, ProfileId = "cmd" },
                            new SessionSnapshot { Id = duplicate, ProfileId = "cmd" },
                            new SessionSnapshot { Id = Guid.NewGuid(), ProfileId = "" },
                        },
                    },
                },
            };

            AppModel model = AppModel.FromSnapshot(snapshot);

            // The session with no profile stays, to open in the default one.
            Assert.Equal(2, model.AllSessions.Count());
            // As agterm's: a saved selection naming no session is no selection.
            Assert.Null(model.SelectedSessionId);
        }
    }
}
