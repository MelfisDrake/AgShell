namespace AgShell.Core.Tests
{
    public class ConfigPathsTests
    {
        [Fact]
        public void TheSettingWinsElseConfigInTheStateDirectory()
        {
            Assert.Equal(@"D:\dotfiles\agshell", ConfigPaths.ConfigDirectory(@"D:\dotfiles\agshell", @"C:\state"));
            Assert.Equal(Path.Combine(@"C:\state", "config"), ConfigPaths.ConfigDirectory(null, @"C:\state"));
            Assert.Equal(Path.Combine(@"C:\state", "config"), ConfigPaths.ConfigDirectory("", @"C:\state"));
            Assert.Equal(Path.Combine(@"C:\config", "restore-denylist.conf"), ConfigPaths.RestoreDenylistPath(@"C:\config"));
        }

        [Fact]
        public void AnEmptyEditorVariableCountsAsUnset()
        {
            Assert.Equal("code -w", ConfigPaths.Editor("code -w", "vim"));
            Assert.Equal("vim", ConfigPaths.Editor("", "vim"));
            Assert.Equal("vim", ConfigPaths.Editor(null, "vim"));
            Assert.Null(ConfigPaths.Editor("", ""));
        }

        [Fact]
        public void FormerConfigFilesMoveOnceAndNeverOverwrite()
        {
            string state = Path.Combine(Path.GetTempPath(), "agshell-config-" + Guid.NewGuid());
            try
            {
                Directory.CreateDirectory(Path.Combine(state, "themes"));
                File.WriteAllText(Path.Combine(state, "themes", "Mine"), "background = #000000");
                foreach (string name in ConfigPaths.FormerStateFiles)
                {
                    File.WriteAllText(Path.Combine(state, name), "old " + name);
                }

                string config = Path.Combine(state, "config");
                Directory.CreateDirectory(config);
                File.WriteAllText(Path.Combine(config, "hooks.conf"), "new");

                List<string> moved = ConfigPaths.MoveFromStateDirectory(state, config, config);

                Assert.Equal(new[] { "keymap.conf", "remotes.conf", "ghostty.conf", "restore-denylist.conf", "themes" }, moved);
                Assert.Equal("old keymap.conf", File.ReadAllText(Path.Combine(config, "keymap.conf")));
                Assert.Equal("new", File.ReadAllText(Path.Combine(config, "hooks.conf")));
                Assert.True(File.Exists(Path.Combine(state, "hooks.conf")));
                Assert.True(File.Exists(Path.Combine(config, "themes", "Mine")));
                Assert.Empty(ConfigPaths.MoveFromStateDirectory(state, config, config));
            }
            finally
            {
                Directory.Delete(state, true);
            }
        }

        [Fact]
        public void WithAChosenFolderOnlyTheDenylistAndThemesFollowIt()
        {
            string state = Path.Combine(Path.GetTempPath(), "agshell-config-" + Guid.NewGuid());
            try
            {
                Directory.CreateDirectory(state);
                File.WriteAllText(Path.Combine(state, "keymap.conf"), "k");
                File.WriteAllText(Path.Combine(state, "restore-denylist.conf"), "d");
                string chosen = Path.Combine(state, "chosen");

                ConfigPaths.MoveFromStateDirectory(state, Path.Combine(state, "config"), chosen);

                Assert.True(File.Exists(Path.Combine(state, "config", "keymap.conf")));
                Assert.True(File.Exists(Path.Combine(chosen, "restore-denylist.conf")));
            }
            finally
            {
                Directory.Delete(state, true);
            }
        }

        [Fact]
        public void InterruptedMigrationKeepsEverySourceOrDestinationAndRetryMovesThemes()
        {
            string state = Path.Combine(Path.GetTempPath(), "agshell-config-" + Guid.NewGuid());
            try
            {
                Directory.CreateDirectory(Path.Combine(state, "themes", "nested"));
                Dictionary<string, string> expected = ConfigPaths.FormerStateFiles.ToDictionary(name => name, name => "original " + name);
                foreach (KeyValuePair<string, string> file in expected)
                {
                    File.WriteAllText(Path.Combine(state, file.Key), file.Value);
                }

                string theme = "background = #123456\nforeground = #abcdef\n";
                File.WriteAllText(Path.Combine(state, "themes", "nested", "Mine"), theme);
                string chosen = Path.Combine(state, "chosen");
                File.WriteAllText(chosen, "blocking fixture");
                string defaults = Path.Combine(state, "config");
                Assert.Throws<IOException>(() => ConfigPaths.MoveFromStateDirectory(state, defaults, chosen));
                foreach (KeyValuePair<string, string> file in expected)
                {
                    string location = file.Key == "restore-denylist.conf" ? state : defaults;
                    Assert.Equal(file.Value, File.ReadAllText(Path.Combine(location, file.Key)));
                }

                Assert.Equal(theme, File.ReadAllText(Path.Combine(state, "themes", "nested", "Mine")));
                File.Delete(chosen);
                Assert.Equal(new[] { "restore-denylist.conf", "themes" }, ConfigPaths.MoveFromStateDirectory(state, defaults, chosen));
                foreach (KeyValuePair<string, string> file in expected)
                {
                    string location = file.Key == "restore-denylist.conf" ? chosen : defaults;
                    Assert.Equal(file.Value, File.ReadAllText(Path.Combine(location, file.Key)));
                }

                Assert.Equal(theme, File.ReadAllText(Path.Combine(chosen, "themes", "nested", "Mine")));
                Assert.Empty(ConfigPaths.MoveFromStateDirectory(state, defaults, chosen));
            }
            finally
            {
                Directory.Delete(state, true);
            }
        }

        [Fact]
        public void TheWelcomeIsDueOnlyOnAFirstLaunch()
        {
            Assert.True(FirstRunWelcome.IsDue(null, false));
            Assert.False(FirstRunWelcome.IsDue(true, false));
            Assert.False(FirstRunWelcome.IsDue(null, true));
            Assert.True(FirstRunWelcome.IsDue(false, false));

            string state = Path.Combine(Path.GetTempPath(), "agshell-welcome-" + Guid.NewGuid());
            try
            {
                Directory.CreateDirectory(state);
                Assert.False(FirstRunWelcome.HasPriorState(state));
                File.WriteAllText(Path.Combine(state, "agshell.lock"), "");
                Assert.False(FirstRunWelcome.HasPriorState(state));
                Directory.CreateDirectory(Path.Combine(state, "windows"));
                Assert.True(FirstRunWelcome.HasPriorState(state));
            }
            finally
            {
                Directory.Delete(state, true);
            }
        }
    }
}
