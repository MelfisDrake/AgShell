using AgShell.Core;
using Xunit;

namespace AgShell.Core.Tests
{
    public class GhosttyConfigTests
    {
        [Fact]
        public void TheStarterChangesNothing()
        {
            GhosttyConfig config = GhosttyConfig.Parse(GhosttyConfig.Starter);

            Assert.Empty(config.Diagnostics);
            Assert.Equal(new GhosttyConfig() with { Diagnostics = config.Diagnostics, Appearance = config.Appearance }, config);
        }

        [Fact]
        public void KnownKeysAreReadAndALaterLineWins()
        {
            GhosttyConfig config = GhosttyConfig.Parse("""
                copy-on-select = clipboard
                clipboard-read = allow
                clipboard-write = ask
                mouse-hide-while-typing = true
                title = "build box"
                cursor-click-to-move = true
                window-padding-x = 4,12
                window-padding-y = 2
                scrollback-limit = 2000000
                clipboard-read = deny
                """);

            Assert.Empty(config.Diagnostics);
            Assert.Equal("clipboard", config.CopyOnSelect);
            Assert.Equal("deny", config.ClipboardRead);
            Assert.Equal("ask", config.ClipboardWrite);
            Assert.True(config.MouseHideWhileTyping);
            Assert.Equal("build box", config.Title);
            Assert.True(config.CursorClickToMove);
            Assert.Equal((4, 12), config.PaddingX);
            Assert.Equal((2, 2), config.PaddingY);
            Assert.Equal(2000, config.ScrollbackLines);
        }

        [Fact]
        public void AnEmptyValueResetsTheKey()
        {
            GhosttyConfig config = GhosttyConfig.Parse("clipboard-read = allow\nclipboard-read =\n");

            Assert.Equal("ask", config.ClipboardRead);
        }

        [Fact]
        public void ProblemsNameTheirLine()
        {
            GhosttyConfig config = GhosttyConfig.Parse("bogus\nclipboard-read = maybe\nkeybind = ctrl+a=select_all\nfont-thicken = true\n");

            Assert.Equal(new[]
            {
                "ghostty.conf:1: expected key = value",
                "ghostty.conf:2: invalid value for clipboard-read: maybe",
                "ghostty.conf:3: keybind is not supported by AgShell's terminals (the settings or keymap.conf cover it)",
                "ghostty.conf:4: unknown key: font-thicken",
            }, config.Diagnostics);
        }

        [Fact]
        public void ConfigFileIncludesAnotherFile()
        {
            string folder = Directory.CreateTempSubdirectory().FullName;
            try
            {
                File.WriteAllText(Path.Combine(folder, "ghostty.conf"), "config-file = extra.conf\nconfig-file = ?missing.conf\nconfig-file = gone.conf\n");
                File.WriteAllText(Path.Combine(folder, "extra.conf"), "mouse-hide-while-typing = true\nconfig-file = extra.conf\n");

                GhosttyConfig config = GhosttyConfig.Load(Path.Combine(folder, "ghostty.conf"));

                Assert.True(config.MouseHideWhileTyping);
                Assert.Equal(new[] { "gone.conf: file not found", "extra.conf: config-file cycle detected" }, config.Diagnostics);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Theory]
        [InlineData("", "clipboard", true)]
        [InlineData("config-file =\n", "false", false)]
        [InlineData("config-file = \"\"\n", "false", false)]
        public void IncludesLoadAfterDirectLinesAndResetDropsPendingFiles(string reset, string copy, bool hide)
        {
            WithFiles(new Dictionary<string, string>
            {
                ["ghostty.conf"] = "config-file = include.conf\nconfig-file = gone.conf\n" + reset + "copy-on-select = false\nmouse-hide-while-typing = false\n",
                ["include.conf"] = "copy-on-select = clipboard\nmouse-hide-while-typing = true\n",
            }, config =>
            {
                Assert.Equal(copy, config.CopyOnSelect);
                Assert.Equal(hide, config.MouseHideWhileTyping);
                if (reset.Length == 0) Assert.Equal(new[] { "gone.conf: file not found" }, config.Diagnostics);
                else Assert.Empty(config.Diagnostics);
            });
        }

        [Fact]
        public void NestedIncludesLoadAfterSiblingIncludes()
        {
            WithFiles(new Dictionary<string, string>
            {
                ["ghostty.conf"] = "config-file = a\nconfig-file = b\n",
                ["a"] = "config-file = c\ncopy-on-select = false\nmouse-hide-while-typing = false\n",
                ["b"] = "copy-on-select = clipboard\nmouse-hide-while-typing = true\n",
                ["c"] = "copy-on-select = true\nmouse-hide-while-typing = false\n",
            }, config =>
            {
                Assert.Equal("true", config.CopyOnSelect);
                Assert.False(config.MouseHideWhileTyping);
                Assert.Empty(config.Diagnostics);
            });
        }

        [Fact]
        public void ResetInsideIncludeKeepsTheRecursiveIndex()
        {
            WithFiles(new Dictionary<string, string>
            {
                ["ghostty.conf"] = "config-file = a\nconfig-file = gone.conf\n",
                ["a"] = "copy-on-select = clipboard\nmouse-hide-while-typing = true\nconfig-file =\nconfig-file = skipped.conf\nconfig-file = applied.conf\n",
                ["skipped.conf"] = "copy-on-select = false\nmouse-hide-while-typing = false\n",
                ["applied.conf"] = "mouse-hide-while-typing =\n",
            }, config =>
            {
                Assert.Equal("clipboard", config.CopyOnSelect);
                Assert.False(config.MouseHideWhileTyping);
                Assert.Empty(config.Diagnostics);
            });
        }

        [Fact]
        public void IncludeProblemsIdentifyRequiredMissingAndInvalidValues()
        {
            WithFiles(new Dictionary<string, string>
            {
                ["ghostty.conf"] = "config-file = ?optional.conf\nconfig-file = required.conf\nconfig-file = invalid.conf\n",
                ["invalid.conf"] = "copy-on-select = wrong\nmouse-hide-while-typing = wrong\n",
            }, config =>
            {
                Assert.Equal("true", config.CopyOnSelect);
                Assert.False(config.MouseHideWhileTyping);
                Assert.Equal(new[]
                {
                    "required.conf: file not found",
                    "invalid.conf:1: invalid value for copy-on-select: wrong",
                    "invalid.conf:2: invalid value for mouse-hide-while-typing: wrong",
                }, config.Diagnostics);
            });
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void InvalidLinesAreDiagnosedWithoutErasingValidValues(bool invalidLast)
        {
            string valid = "copy-on-select = clipboard\nmouse-hide-while-typing = true\n";
            string invalid = "copy-on-select = wrong\nmouse-hide-while-typing = wrong\n";
            GhosttyConfig config = GhosttyConfig.Parse(invalidLast ? valid + invalid : invalid + valid);

            Assert.Equal("clipboard", config.CopyOnSelect);
            Assert.True(config.MouseHideWhileTyping);
            Assert.Equal(2, config.Diagnostics.Count);
            Assert.Contains("invalid value for copy-on-select: wrong", config.Diagnostics[0]);
            Assert.Contains("invalid value for mouse-hide-while-typing: wrong", config.Diagnostics[1]);
        }

        private static void WithFiles(Dictionary<string, string> files, Action<GhosttyConfig> check)
        {
            string folder = Directory.CreateTempSubdirectory("agshell-r8-").FullName;
            try
            {
                foreach ((string name, string content) in files) File.WriteAllText(Path.Combine(folder, name), content);
                Dictionary<string, byte[]> originals = files.Keys.ToDictionary(name => name, name => File.ReadAllBytes(Path.Combine(folder, name)));
                check(GhosttyConfig.Load(Path.Combine(folder, "ghostty.conf")));
                Assert.Equal(files.Count, Directory.GetFiles(folder).Length);
                foreach ((string name, byte[] content) in originals) Assert.Equal(content, File.ReadAllBytes(Path.Combine(folder, name)));
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void DesktopNotificationsAreOnUntilTurnedOff()
        {
            Assert.True(GhosttyConfig.Parse(string.Empty).DesktopNotifications);
            Assert.False(GhosttyConfig.Parse("desktop-notifications = false").DesktopNotifications);
            Assert.True(GhosttyConfig.Parse("desktop-notifications = false\ndesktop-notifications =").DesktopNotifications);
            Assert.Contains("invalid value for desktop-notifications: no", Assert.Single(GhosttyConfig.Parse("desktop-notifications = no").Diagnostics));
        }

        [Fact]
        public void TerminalNotificationsAreLimitedAsGhosttys()
        {
            NotificationRateLimit limit = new NotificationRateLimit();
            Assert.True(limit.Allow(string.Empty, "a", TimeSpan.FromSeconds(10)));
            Assert.False(limit.Allow(string.Empty, "b", TimeSpan.FromSeconds(10.5)));
            Assert.True(limit.Allow(string.Empty, "b", TimeSpan.FromSeconds(11)));
            Assert.False(limit.Allow(string.Empty, "b", TimeSpan.FromSeconds(15.9)));
            Assert.True(limit.Allow("t", "b", TimeSpan.FromSeconds(12)));
            Assert.True(limit.Allow("t", "b", TimeSpan.FromSeconds(17)));
        }
    }
}
