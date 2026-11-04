using System.Text.Json;

namespace AgShell.Core.Tests
{
    public class SettingsStoreTests
    {
        private static string TempPath() => Path.Combine(Path.GetTempPath(), "agshell-settings-" + Guid.NewGuid() + ".json");

        private static void DeleteAll(string path)
        {
            foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
            {
                File.Delete(file);
            }
        }

        [Fact]
        public void KeysAreWrittenSortedAsAgtermsSortedKeys()
        {
            string path = TempPath();
            try
            {
                new SettingsStore(path).Save(new AppSettings { Theme = "x", FontFamily = "y", CursorBlink = false, ActiveStatusColorHex = "#010203" });

                string[] keys = JsonDocument.Parse(File.ReadAllText(path)).RootElement.EnumerateObject().Select(property => property.Name).ToArray();
                Assert.Equal(new[] { "activeStatusColorHex", "cursorBlink", "fontFamily", "theme" }, keys);
            }
            finally
            {
                DeleteAll(path);
            }
        }

        [Fact]
        public void OldNamesAreReadOnceIntoAgtermsNamesAndNotWrittenBack()
        {
            string path = TempPath();
            try
            {
                File.WriteAllText(path, """{ "activeStatusColor": "#111111", "blockedStatusColorHex": "#222222", "blockedStatusColor": "#333333", "notificationSound": "mail", "blockedStatusSound": "Alarm01" }""");
                SettingsStore store = new SettingsStore(path);
                AppSettings settings = store.Load();

                Assert.Equal("#111111", settings.ActiveStatusColorHex);
                Assert.Equal("#222222", settings.BlockedStatusColorHex);
                Assert.Equal("mail", settings.NotificationSoundName);
                Assert.Equal("Alarm01", settings.BlockedStatusSoundName);

                store.Save(settings);
                string text = File.ReadAllText(path);
                Assert.DoesNotContain("\"activeStatusColor\"", text);
                Assert.DoesNotContain("\"notificationSound\"", text);
                Assert.Contains("\"notificationSoundName\": \"mail\"", text);
            }
            finally
            {
                DeleteAll(path);
            }
        }

        [Fact]
        public void AnUnreadableFileGivesTheSeededDefaultsAndAFailedWriteIsDropped()
        {
            string path = TempPath();
            try
            {
                File.WriteAllText(path, "{ \"fontSize\": 20 }");
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    SettingsStore store = new SettingsStore(path);
                    Assert.Equal(Themes.DefaultName, store.Load().Theme);
                    Assert.False(store.Save(new AppSettings()));
                }

                Assert.False(new SettingsStore(Path.Combine(path, "settings.json")).Save(new AppSettings()));
            }
            finally
            {
                DeleteAll(path);
            }
        }

        [Fact]
        public void FailedWriteKeepsOriginalBytesAndRetryPersistsExplicitLegacyValues()
        {
            string path = TempPath();
            try
            {
                string original = """{"activeStatusColor":"#123456","notificationSound":"mail","fontSize":14,"cursorBlink":false,"backgroundOpacity":1,"theme":"Mine","configDirectory":"C:\\fixture\\chosen"}""";
                File.WriteAllText(path, original);
                SettingsStore store = new SettingsStore(path);
                AppSettings settings = store.Load();
                Assert.Equal("#123456", settings.ActiveStatusColorHex);
                Assert.Equal("mail", settings.NotificationSoundName);
                Assert.Equal(14, settings.FontSize);
                Assert.False(settings.CursorBlink);
                Assert.Equal(1, settings.BackgroundOpacity);
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    Assert.False(store.Save(settings));
                    Assert.Equal(original, File.ReadAllText(path));
                }

                Assert.True(store.Save(settings));
                AppSettings reloaded = new SettingsStore(path).Load();
                Assert.Equal(settings.ActiveStatusColorHex, reloaded.ActiveStatusColorHex);
                Assert.Equal(settings.NotificationSoundName, reloaded.NotificationSoundName);
                Assert.Equal(settings.FontSize, reloaded.FontSize);
                Assert.Equal(settings.CursorBlink, reloaded.CursorBlink);
                Assert.Equal(settings.BackgroundOpacity, reloaded.BackgroundOpacity);
                Assert.Equal(settings.Theme, reloaded.Theme);
                Assert.Equal(settings.ConfigDirectory, reloaded.ConfigDirectory);
                Assert.DoesNotContain("\"activeStatusColor\"", File.ReadAllText(path));
                Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".corrupt-*"));
            }
            finally
            {
                DeleteAll(path);
            }
        }

        [Fact]
        public void FailedReadKeepsBytesAndRetryReadsTheOriginalSettings()
        {
            string path = TempPath();
            try
            {
                string original = """{"fontSize":21,"theme":"Mine","cursorBlink":false}""";
                File.WriteAllText(path, original);
                SettingsStore store = new SettingsStore(path);
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    Assert.Equal(Themes.DefaultName, store.Load().Theme);
                }

                Assert.Equal(original, File.ReadAllText(path));
                Assert.True(store.WriteBlocked);
                Assert.False(store.Save(new AppSettings { FontSize = 30 }));
                Assert.Equal(original, File.ReadAllText(path));
                Assert.Equal(21, store.Load().FontSize);
                Assert.False(store.WriteBlocked);
                Assert.True(store.Save(store.Load()));
                Assert.Equal("Mine", store.Load().Theme);
                Assert.False(store.Load().CursorBlink);
                Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".corrupt-*"));
            }
            finally
            {
                DeleteAll(path);
            }
        }

        [Fact]
        public void FailedCorruptBackupBlocksReplacementUntilLoadCanPreserveIt()
        {
            string path = TempPath();
            try
            {
                string original = "{broken original";
                File.WriteAllText(path, original);
                SettingsStore store = new SettingsStore(path);
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    store.Load();
                    Assert.True(store.WriteBlocked);
                    Assert.False(store.Save(new AppSettings()));
                    Assert.Equal(original, File.ReadAllText(path));
                }

                Assert.False(store.Save(new AppSettings()));
                Assert.Equal(original, File.ReadAllText(path));
                AppSettings recovered = store.Load();
                Assert.False(store.WriteBlocked);
                string backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".corrupt-*"));
                Assert.Equal(original, File.ReadAllText(backup));
                Assert.True(store.Save(recovered));
                Assert.Equal(original, File.ReadAllText(backup));
            }
            finally
            {
                DeleteAll(path);
            }
        }

        [Fact]
        public void RepeatedCorruptionPreservesEachOriginalInDistinctBackups()
        {
            string path = TempPath();
            try
            {
                SettingsStore store = new SettingsStore(path);
                File.WriteAllText(path, "{first");
                store.Load();
                File.WriteAllText(path, "{second");
                store.Load();
                string[] backups = Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".corrupt-*");
                Assert.Equal(2, backups.Length);
                Assert.Equal(new[] { "{first", "{second" }, backups.Select(File.ReadAllText).Order(StringComparer.Ordinal));
                Assert.False(store.WriteBlocked);
                Assert.True(store.Save(new AppSettings { FontSize = 19 }));
                Assert.Equal(19, store.Load().FontSize);
            }
            finally
            {
                DeleteAll(path);
            }
        }

        [Fact]
        public void ConfirmedMissingFileReleasesTheReadFailureBlock()
        {
            string path = TempPath();
            try
            {
                File.WriteAllText(path, "{}");
                SettingsStore store = new SettingsStore(path);
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    store.Load();
                    Assert.True(store.WriteBlocked);
                }

                File.Delete(path);
                store.Load();
                Assert.False(store.WriteBlocked);
                Assert.True(store.Save(new AppSettings { FontSize = 18 }));
                Assert.Equal(18, store.Load().FontSize);
            }
            finally
            {
                DeleteAll(path);
            }
        }

        [Fact]
        public void APatchStoresDefaultsAsNullAsAgtermsBindings()
        {
            AppSettings settings = new AppSettings
            {
                StatusReset = "enter", TaskbarFlash = "once", ConfirmCloseSession = true, CloseGraceUndoEnabled = false, ToolbarMode = "normal",
                SidebarFontSize = 15, InactivePaneMuteStrength = 2, BackgroundOpacity = 0.5, NotificationSoundName = "mail", ActiveStatusShape = "star",
            };
            using JsonDocument patch = JsonDocument.Parse("""
                {"statusReset":"firstKey","taskbarFlash":"off","confirmCloseSession":false,"closeGraceUndoEnabled":true,"toolbarMode":"compact",
                 "sidebarFontSize":13,"inactivePaneMuteStrength":5,"backgroundOpacity":1,"notificationSoundName":"none","activeStatusShape":"circle",
                 "rightClickPaste":true,"notificationsEnabled":true,"newSessionDirectory":"home","newSessionPlacement":"end","autoFollowAttention":"off",
                 "autoFollowStayOnActive":false,"mouseScrollMultiplier":3,"workspaceRowClickExpands":true,"backgroundBlur":false}
                """);

            Assert.Empty(SettingsStore.Patch(settings, patch.RootElement));

            string path = TempPath();
            try
            {
                new SettingsStore(path).Save(settings);
                Assert.Empty(JsonDocument.Parse(File.ReadAllText(path)).RootElement.EnumerateObject());
            }
            finally
            {
                DeleteAll(path);
            }
        }

        [Fact]
        public void ValuesAreTakenAsAgtermTakesThem()
        {
            AppSettings settings = new AppSettings { MouseScrollMultiplier = 20, QuickTerminalSizePercent = 55 };

            Assert.Equal(20, settings.EffectiveMouseScrollMultiplier);
            Assert.Null(settings.QuickTerminalSizeSetting);
            Assert.Equal(90, settings.EffectiveQuickTerminalSizePercent);
            Assert.Equal("none", new AppSettings().EffectiveNotificationSound);
            Assert.Equal("none", new AppSettings { NotificationSoundName = "bogus" }.EffectiveNotificationSound);
            Assert.Equal(AppModel.DefaultFontSize, new AppSettings().EffectiveFontSize);
            Assert.Equal(70, new AppSettings { QuickTerminalSizePercent = 70 }.QuickTerminalSizeSetting);
        }

        [Fact]
        public void HiddenElementsFollowAgtermsResolvedHiddenInterfaceElements()
        {
            AppSettings settings = new AppSettings
            {
                HiddenInterfaceElements = new List<string> { "workspaceName", "split", "future" },
                ShownInterfaceElements = new List<string> { "workspaceName", "split" },
            };

            Assert.Equal(new[] { "split", "overlayToggle", "customCommands" }.OrderBy(x => x), settings.EffectiveHiddenInterfaceElements.OrderBy(x => x));
        }
    }
}
