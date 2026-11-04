namespace AgShell.Core.Tests
{
    public sealed class StateStoreTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "agshell-tests-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void MissingFileLoadsAsNull()
        {
            StateStore store = new StateStore(Path.Combine(directory, "state.json"));

            Assert.Null(store.Load());
        }

        [Fact]
        public void SaveAndLoadRoundTrip()
        {
            StateStore store = new StateStore(Path.Combine(directory, "nested", "state.json"));
            StateSnapshot snapshot = new StateSnapshot
            {
                Workspaces = new List<WorkspaceSnapshot> { new WorkspaceSnapshot { Id = Guid.NewGuid(), Name = "работа" } },
                Window = new WindowPlacement { Left = 10, Top = 20, Width = 800, Height = 600, Maximized = true },
                Sidebar = new SidebarSettings { Visible = false, Width = 260 },
            };

            store.Save(snapshot);
            StateSnapshot? loaded = store.Load();

            Assert.NotNull(loaded);
            Assert.Equal("работа", loaded.Workspaces[0].Name);
            Assert.Equal(snapshot.Window, loaded.Window);
            Assert.Equal(snapshot.Sidebar, loaded.Sidebar);
            Assert.False(File.Exists(store.FilePath + ".tmp"));
            Assert.Contains("\"работа\"", File.ReadAllText(store.FilePath));
        }

        [Fact]
        public void AnEarlierBackgroundReadsUnderAgtermsNames()
        {
            string path = Path.Combine(directory, "state.json");
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, """
                { "version": 1, "workspaces": [ { "id": "6f1d9a6e-1c2b-4c58-9b55-0f4a8f0f4e11", "name": "w", "sessions": [ {
                    "id": "0b0f3c4e-5a1d-4f7e-8f3a-2d9e6c1b7a10", "shell": "cmd",
                    "background": { "kind": "image", "path": "C:\\pics\\a.png", "position": "top", "repeat": true },
                    "paneBackgrounds": { "right": { "kind": "text", "text": "x", "color": "#112233", "position": "center-left" } } } ] } ] }
                """);

            StateSnapshot loaded = new StateStore(path).Load()!;
            SessionSnapshot session = loaded.Workspaces[0].Sessions[0];
            Assert.Equal(("C:\\pics\\a.png", "top-center", true), (session.Background!.ImagePath, session.Background.Position, session.Background.Repeats));
            Assert.Equal(("#112233", "center-left"), (session.PaneBackgrounds!["right"].ColorHex, session.PaneBackgrounds["right"].Position));

            new StateStore(path).Save(loaded);
            string saved = File.ReadAllText(path);
            Assert.Contains("\"imagePath\"", saved);
            Assert.DoesNotContain("\"path\"", saved);
            Assert.DoesNotContain("\"repeat\"", saved);
        }

        [Fact]
        public void CorruptFileIsMovedAside()
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "state.json");
            File.WriteAllText(path, "{ not json");
            StateStore store = new StateStore(path);

            Assert.Null(store.Load());
            Assert.False(File.Exists(path));
            string backup = Assert.Single(Directory.GetFiles(directory, "state.json.corrupt-*"));
            Assert.Equal("{ not json", File.ReadAllText(backup));
        }

        [Fact]
        public void FailedWritePreservesTheOriginalAndRetryPersistsTheSameIdentities()
        {
            string path = Path.Combine(directory, "state.json");
            StateStore store = new StateStore(path);
            StateSnapshot original = new StateSnapshot
            {
                Workspaces = new List<WorkspaceSnapshot> { new WorkspaceSnapshot { Id = Guid.NewGuid(), Name = "before", Sessions = new List<SessionSnapshot> { new SessionSnapshot { Id = Guid.NewGuid(), ProfileId = "cmd", Cwd = @"C:\temp", Flagged = true } } } },
            };
            store.Save(original);
            byte[] before = File.ReadAllBytes(path);
            StateSnapshot changed = original with { Sidebar = new SidebarSettings { Visible = false, Width = 290 } };
            using (FileStream locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Exception error = Assert.ThrowsAny<Exception>(() => store.Save(changed));
                Assert.True(error is IOException or UnauthorizedAccessException);
            }

            Assert.Equal(before, File.ReadAllBytes(path));
            store.Save(changed);
            StateSnapshot loaded = store.Load()!;
            Assert.Equal(original.Workspaces[0].Id, loaded.Workspaces[0].Id);
            Assert.Equal(original.Workspaces[0].Sessions[0], loaded.Workspaces[0].Sessions[0]);
            Assert.Equal(changed.Sidebar, loaded.Sidebar);
            Assert.Empty(Directory.GetFiles(directory, "*.corrupt-*"));
        }

        [Fact]
        public void FailedReadBlocksWritesUntilTheSameStoreSuccessfullyReloads()
        {
            string path = Path.Combine(directory, "state.json");
            StateStore store = new StateStore(path);
            StateSnapshot original = new StateSnapshot { Workspaces = new List<WorkspaceSnapshot> { new WorkspaceSnapshot { Id = Guid.NewGuid(), Name = "must survive" } } };
            store.Save(original);
            byte[] before = File.ReadAllBytes(path);
            using (FileStream locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.Null(store.Load());
                Assert.True(store.WriteBlocked);
            }

            Assert.Throws<IOException>(() => store.Save(new StateSnapshot()));
            Assert.Equal(before, File.ReadAllBytes(path));
            StateSnapshot loaded = store.Load()!;
            Assert.False(store.WriteBlocked);
            Assert.Equal(original.Workspaces[0].Id, loaded.Workspaces[0].Id);
            store.Save(loaded);
            Assert.Equal("must survive", new StateStore(path).Load()!.Workspaces[0].Name);
        }

        [Fact]
        public void AFailedQuarantineKeepsSourceAndBlocksWritesUntilItCanBePreserved()
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "state.json");
            File.WriteAllText(path, "{ broken");
            byte[] before = File.ReadAllBytes(path);
            StateStore store = new StateStore(path);
            using (FileStream locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.Null(store.Load());
                Assert.True(store.WriteBlocked);
            }

            Assert.Throws<IOException>(() => store.Save(new StateSnapshot()));
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Null(store.Load());
            Assert.False(store.WriteBlocked);
            string backup = Assert.Single(Directory.GetFiles(directory, "state.json.corrupt-*"));
            Assert.Equal(before, File.ReadAllBytes(backup));
            store.Save(new StateSnapshot());
            Assert.NotNull(store.Load());
        }

        [Fact]
        public void RepeatedQuarantinesNeverReplaceAnEarlierOriginal()
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "state.json");
            StateStore store = new StateStore(path);
            foreach (string source in new[] { "{ first broken", "{ second broken" })
            {
                File.WriteAllText(path, source);
                Assert.Null(store.Load());
            }

            Assert.Equal(new[] { "{ first broken", "{ second broken" }, Directory.GetFiles(directory, "state.json.corrupt-*").Select(File.ReadAllText).Order(StringComparer.Ordinal));
        }

        [Theory]
        [InlineData("{ broken", ".corrupt-*")]
        [InlineData("{\"version\":99,\"workspaces\":[]}", ".version-99-*")]
        [InlineData("{\"version\":99,\"workspaces\":{\"future\":true}}", ".version-99-*")]
        public void UnusableStateKeepsItsExactBytesSeparatelyFromNewState(string source, string suffix)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "state.json");
            File.WriteAllText(path, source);
            byte[] before = File.ReadAllBytes(path);
            StateStore store = new StateStore(path);
            Assert.Null(store.Load());
            string backup = Assert.Single(Directory.GetFiles(directory, "state.json" + suffix));
            Assert.Equal(before, File.ReadAllBytes(backup));
            store.Save(new StateSnapshot());
            Assert.Equal(before, File.ReadAllBytes(backup));
            Assert.NotNull(store.Load());
        }

        [Fact]
        public void ABadOptionalFieldReadsAsUnsetAndKeepsTheRest()
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "state.json");
            Guid workspace = Guid.NewGuid();
            Guid session = Guid.NewGuid();
            File.WriteAllText(path, $$"""
                {
                  "version": 1,
                  "selectedSessionId": "not-a-guid",
                  "sessionRecency": [ "{{session}}", 5 ],
                  "window": { "left": "x" },
                  "focusEnabled": "yes",
                  "workspaces": [ {
                    "id": "{{workspace}}", "name": "работа", "collapsed": 3,
                    "sessions": [ {
                      "id": "{{session}}", "profileId": "pwsh", "cwd": "C:\\work",
                      "fontSize": "big", "splitRatio": 0.4, "hasSplit": null, "paneIds": [ 1 ], "background": 7, "flagged": true
                    } ]
                  } ]
                }
                """);
            StateStore store = new StateStore(path);

            StateSnapshot? loaded = store.Load();

            Assert.NotNull(loaded);
            Assert.Null(loaded.SelectedSessionId);
            Assert.Empty(loaded.SessionRecency);
            Assert.Null(loaded.Window);
            Assert.False(loaded.FocusEnabled);
            WorkspaceSnapshot read = Assert.Single(loaded.Workspaces);
            Assert.Equal(("работа", false), (read.Name, read.Collapsed));
            SessionSnapshot one = Assert.Single(read.Sessions);
            Assert.Equal((session, "pwsh", @"C:\work"), (one.Id, one.ProfileId, one.Cwd));
            Assert.Equal((null, 0.4, false, true), (one.FontSize, one.SplitRatio, one.HasSplit, one.Flagged));
            Assert.Null(one.PaneIds);
            Assert.Null(one.Background);
            Assert.True(File.Exists(path));
            store.Save(loaded);
            StateSnapshot again = store.Load()!;
            Assert.Equal(read.Id, again.Workspaces[0].Id);
            Assert.Equal(one.Id, again.Workspaces[0].Sessions[0].Id);
            Assert.Equal(one.Cwd, again.Workspaces[0].Sessions[0].Cwd);
            Assert.True(again.Workspaces[0].Sessions[0].Flagged);
            Assert.Empty(Directory.GetFiles(directory, "*.corrupt-*"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("\"sessionRecency\": null,")]
        [InlineData("\"sessionRecency\": [5],")]
        [InlineData("\"sessionRecency\": {\"bad\": true},")]
        [InlineData("valid")]
        public void UnsetRecencyRestoresTheModelAndRoundTripsWithoutLosingNeighbors(string recency)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "state.json");
            Guid workspace = Guid.NewGuid();
            Guid first = Guid.NewGuid();
            Guid second = Guid.NewGuid();
            string pane = Guid.NewGuid().ToString();
            bool valid = recency == "valid";
            if (valid)
            {
                recency = $"\"sessionRecency\": [\"{first}\", \"{second}\"],";
            }

            File.WriteAllText(path, $$"""
                { "version": 1, {{recency}} "selectedSessionId": "{{second}}",
                  "sidebar": { "visible": false, "width": 280 },
                  "workspaces": [ { "id": "{{workspace}}", "name": "saved", "sessions": [
                    { "id": "{{first}}", "profileId": "cmd", "name": "first", "cwd": "C:\\one", "flagged": true,
                      "paneIds": { "left": "{{pane}}" }, "restorePin": "echo saved" },
                    { "id": "{{second}}", "profileId": "pwsh", "name": "second", "cwd": "C:\\two" }
                  ] } ] }
                """);
            StateStore store = new StateStore(path);

            StateSnapshot loaded = Assert.IsType<StateSnapshot>(store.Load());
            AppModel model = AppModel.FromSnapshot(loaded, launchRestore: true);
            Assert.NotNull(loaded.SessionRecency);
            Assert.Equal(valid ? new[] { first, second } : Array.Empty<Guid>(), loaded.SessionRecency);
            StateSnapshot restored = model.ToSnapshot();
            Assert.Equal(valid ? new[] { second, first } : new[] { second }, restored.SessionRecency);
            Assert.Equal(second, restored.SelectedSessionId);
            Assert.Equal(workspace, Assert.Single(restored.Workspaces).Id);
            Assert.Equal(new[] { first, second }, restored.Workspaces[0].Sessions.Select(session => session.Id));
            SessionSnapshot one = restored.Workspaces[0].Sessions[0];
            Assert.Equal(("first", "cmd", @"C:\one", true, "echo saved", pane),
                (one.Name, one.ProfileId, one.Cwd, one.Flagged, one.RestorePin, one.PaneIds!["left"]));
            Assert.Equal(("second", "pwsh", @"C:\two"),
                (restored.Workspaces[0].Sessions[1].Name, restored.Workspaces[0].Sessions[1].ProfileId, restored.Workspaces[0].Sessions[1].Cwd));
            store.Save(restored with { Sidebar = loaded.Sidebar });
            StateSnapshot roundTrip = Assert.IsType<StateSnapshot>(store.Load());
            Assert.Equal(restored.SessionRecency, roundTrip.SessionRecency);
            Assert.Equal(loaded.Sidebar, roundTrip.Sidebar);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(restored),
                System.Text.Json.JsonSerializer.Serialize(AppModel.FromSnapshot(roundTrip).ToSnapshot()));
            Assert.Single(Directory.GetFiles(directory));
        }

        [Fact]
        public void AFileWithNoVersionLoadsAsNoStateAsInAgterm()
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "state.json");
            File.WriteAllText(path, """{ "workspaces": [] }""");

            Assert.Null(new StateStore(path).Load());
            Assert.Single(Directory.GetFiles(directory, "state.json.corrupt-*"));
        }

        [Fact]
        public void ABadProfileOpensTheSessionInTheDefaultOne()
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "state.json");
            Guid first = Guid.NewGuid();
            Guid second = Guid.NewGuid();
            File.WriteAllText(path, $$"""
                { "version": 1, "sessionRecency": [], "workspaces": [ { "id": "{{Guid.NewGuid()}}", "name": "w", "sessions": [
                  { "id": "{{first}}", "profileId": 5, "cwd": "C:\\a" }, { "id": "{{second}}", "cwd": "C:\\b" } ] } ] }
                """);

            StateSnapshot loaded = new StateStore(path).Load()!;
            AppModel model = AppModel.FromSnapshot(loaded);
            model.AdoptDefaultProfile("pwsh");
            Assert.Empty(Directory.GetFiles(directory, "state.json.corrupt-*"));
            Assert.Equal(("pwsh", "pwsh"), (model.FindSession(first)!.ProfileId, model.FindSession(second)!.ProfileId));
        }

        [Fact]
        public void ABadIdentityStillMovesTheFileAside()
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "state.json");
            File.WriteAllText(path, """{ "version": 1, "workspaces": [ { "id": "bad", "name": "w", "sessions": [] } ] }""");
            StateStore store = new StateStore(path);

            Assert.Null(store.Load());
            Assert.Single(Directory.GetFiles(directory, "state.json.corrupt-*"));
        }
    }
}
