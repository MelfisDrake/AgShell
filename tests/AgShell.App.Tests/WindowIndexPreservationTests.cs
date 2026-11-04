using System.IO;
using System.Reflection;
using System.Text.Json;
using AgShell.Core;

namespace AgShell.App.Tests
{
    public sealed class WindowIndexPreservationTests : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "agshell-index-" + Guid.NewGuid().ToString("N"));

        public void Dispose() => Directory.Delete(directory, true);

        [Theory]
        [InlineData("{broken")]
        [InlineData("{\"version\":1,\"windows\":[null]}")]
        [InlineData("{\"version\":1,\"windows\":[{}]}")]
        [InlineData("{\"version\":1,\"windows\":[{\"id\":\"3cf26811-c150-45c2-9255-6fd7ef22e68a\",\"stateFile\":null}]}")]
        public void BrokenIndexRecoversEverySurvivingStateWithoutChangingTheirBytes(string json)
        {
            Directory.CreateDirectory(Path.Combine(directory, "windows"));
            Guid second = Guid.NewGuid();
            string firstPath = Path.Combine(directory, "state.json");
            string secondPath = Path.Combine(directory, "windows", second + ".json");
            new StateStore(firstPath).Save(Snapshot("first"));
            new StateStore(secondPath).Save(Snapshot("second"));
            byte[] first = File.ReadAllBytes(firstPath);
            byte[] other = File.ReadAllBytes(secondPath);
            File.WriteAllText(Path.Combine(directory, "windows.json"), json);

            WindowLibrary library = (WindowLibrary)Activator.CreateInstance(typeof(WindowLibrary), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { directory }, null)!;
            List<WindowEntry> entries = (List<WindowEntry>)typeof(WindowLibrary).GetField("entries", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(library)!;

            Assert.Equal(new[] { "state.json", Path.Combine("windows", second + ".json") }, entries.Select(entry => entry.StateFile));
            Assert.All(entries, entry => Assert.True(entry.Open && entry.Recovered));
            Assert.Equal(first, File.ReadAllBytes(firstPath));
            Assert.Equal(other, File.ReadAllBytes(secondPath));
            Assert.Equal("first", new StateStore(firstPath).Load()!.Workspaces[0].Name);
            Assert.Equal("second", new StateStore(secondPath).Load()!.Workspaces[0].Name);
            string backup = Assert.Single(Directory.GetFiles(directory, "windows.json.corrupt-*"));
            Assert.Equal(json, File.ReadAllText(backup));
        }

        [Fact]
        public void LegacyBareIndexKeepsItsIdentityAndMissingNameGetsTheExistingDefault()
        {
            Directory.CreateDirectory(directory);
            Guid id = Guid.NewGuid();
            File.WriteAllText(Path.Combine(directory, "windows.json"), "[{\"id\":\"" + id + "\",\"stateFile\":\"state.json\",\"open\":false}]");
            WindowLibrary library = (WindowLibrary)Activator.CreateInstance(typeof(WindowLibrary), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { directory }, null)!;
            WindowEntry entry = Assert.Single(library.Entries);
            Assert.Equal(id, entry.Id);
            Assert.Equal("state.json", entry.StateFile);
            Assert.Equal("window 1", entry.Name);
            Assert.False(entry.Open);
            Assert.False(entry.Recovered);
        }

        [Fact]
        public void FailedIndexWriteKeepsOriginalBytesAndASnapshotSaveCanRetry()
        {
            Directory.CreateDirectory(directory);
            new StateStore(Path.Combine(directory, "state.json")).Save(Snapshot("first"));
            WindowLibrary library = (WindowLibrary)Activator.CreateInstance(typeof(WindowLibrary), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { directory }, null)!;
            string path = Path.Combine(directory, "windows.json");
            byte[] before = File.ReadAllBytes(path);
            library.Entries[0].Name = "renamed but not yet persisted";
            using (FileStream locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                library.SaveEntries();
                Assert.True(library.IndexUnsaved);
            }

            Assert.Equal(before, File.ReadAllBytes(path));
            library.RetryUnsavedIndex();
            Assert.False(library.IndexUnsaved);
            WindowLibrary reopened = (WindowLibrary)Activator.CreateInstance(typeof(WindowLibrary), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { directory }, null)!;
            Assert.Equal(library.Entries[0].Id, reopened.Entries[0].Id);
            Assert.Equal("renamed but not yet persisted", reopened.Entries[0].Name);
            Assert.Equal("first", new StateStore(Path.Combine(directory, "state.json")).Load()!.Workspaces[0].Name);
        }

        [Fact]
        public void FailedSettingsSaveDoesNotPublishASuccessfulSaveEvent()
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "settings.json");
            SettingsStore store = new SettingsStore(path);
            Assert.True(store.Save(new AppSettings { FontSize = 19, NotificationsEnabled = false }));
            byte[] before = File.ReadAllBytes(path);
            WindowLibrary library = (WindowLibrary)Activator.CreateInstance(typeof(WindowLibrary), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { directory }, null)!;
            int saved = 0;
            library.SettingsSaved += () => saved++;
            library.Settings.FontSize = 21;
            using (FileStream locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.False(library.SaveSettings());
            }

            Assert.Equal(0, saved);
            Assert.Equal(before, File.ReadAllBytes(path));
            Assert.Equal(19, store.Load().FontSize);
        }

        [Theory]
        [InlineData("{broken", "corrupt")]
        [InlineData("{\"version\":99,\"windows\":{\"future\":true}}", "version-99")]
        public void FailedIndexBackupBlocksReplacementUntilRestartCanPreserveItsBytes(string source, string reason)
        {
            Directory.CreateDirectory(directory);
            new StateStore(Path.Combine(directory, "state.json")).Save(Snapshot("first"));
            string path = Path.Combine(directory, "windows.json");
            File.WriteAllText(path, source);
            WindowLibrary library;
            using (FileStream locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                library = (WindowLibrary)Activator.CreateInstance(typeof(WindowLibrary), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { directory }, null)!;
                Assert.True(library.IndexWriteBlocked);
                Assert.True(library.IndexUnsaved);
            }

            library.RetryUnsavedIndex();
            Assert.Equal(source, File.ReadAllText(path));
            WindowLibrary restarted = (WindowLibrary)Activator.CreateInstance(typeof(WindowLibrary), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { directory }, null)!;
            Assert.False(restarted.IndexWriteBlocked);
            Assert.False(restarted.IndexUnsaved);
            string backup = Assert.Single(Directory.GetFiles(directory, "windows.json." + reason + "-*"));
            Assert.Equal(source, File.ReadAllText(backup));
            Assert.Equal("first", new StateStore(Path.Combine(directory, "state.json")).Load()!.Workspaces[0].Name);
        }

        [Fact]
        public void FailedIndexReadCannotReplaceOriginalAfterUnlockAndRestartLoadsItsMetadata()
        {
            Directory.CreateDirectory(directory);
            new StateStore(Path.Combine(directory, "state.json")).Save(Snapshot("first"));
            Guid id = Guid.NewGuid();
            string path = Path.Combine(directory, "windows.json");
            string source = JsonSerializer.Serialize(new { version = 1, frontmost = id, windows = new[] { new { id, name = "must survive", open = true, stateFile = "state.json" } } });
            File.WriteAllText(path, source);
            WindowLibrary library;
            using (FileStream locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                library = (WindowLibrary)Activator.CreateInstance(typeof(WindowLibrary), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { directory }, null)!;
                Assert.True(library.IndexWriteBlocked);
            }

            library.SaveEntries();
            Assert.True(library.IndexUnsaved);
            Assert.Equal(source, File.ReadAllText(path));
            WindowLibrary restarted = (WindowLibrary)Activator.CreateInstance(typeof(WindowLibrary), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { directory }, null)!;
            Assert.False(restarted.IndexWriteBlocked);
            WindowEntry entry = Assert.Single(restarted.Entries);
            Assert.Equal(id, entry.Id);
            Assert.Equal("must survive", entry.Name);
            Assert.Empty(Directory.GetFiles(directory, "windows.json.*-*"));
        }

        [Fact]
        public void UnreadableSettingsDoNotMigrateFilesUsingAnUnknownConfigDirectory()
        {
            Directory.CreateDirectory(directory);
            string settings = Path.Combine(directory, "settings.json");
            string original = "{\"configDirectory\":\"chosen\"}";
            File.WriteAllText(settings, original);
            File.WriteAllText(Path.Combine(directory, "ghostty.conf"), "copy-on-select = clipboard");
            using (FileStream locked = new FileStream(settings, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                WindowLibrary library = (WindowLibrary)Activator.CreateInstance(typeof(WindowLibrary), BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { directory }, null)!;
                Assert.True(library.SettingsStore.WriteBlocked);
                Assert.True(File.Exists(Path.Combine(directory, "ghostty.conf")));
                Assert.False(Directory.Exists(Path.Combine(directory, "config")));
            }

            Assert.Equal(original, File.ReadAllText(settings));
            Assert.Equal("copy-on-select = clipboard", File.ReadAllText(Path.Combine(directory, "ghostty.conf")));
        }

        private static StateSnapshot Snapshot(string name) => new StateSnapshot
        {
            Workspaces = new List<WorkspaceSnapshot>
            {
                new WorkspaceSnapshot { Id = Guid.NewGuid(), Name = name, Sessions = new List<SessionSnapshot> { new SessionSnapshot { Id = Guid.NewGuid(), ProfileId = "cmd", Cwd = @"C:\temp", Flagged = true } } },
            },
        };
    }
}
