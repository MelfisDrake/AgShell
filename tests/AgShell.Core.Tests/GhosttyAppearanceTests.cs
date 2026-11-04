using System.Text.Json;
using AgShell.Core;
using Xunit;

namespace AgShell.Core.Tests
{
    public class GhosttyAppearanceTests
    {
        [Fact]
        public void ExplicitDefaultsSurviveSaveWithoutConvertingAbsenceToAChoice()
        {
            string folder = Directory.CreateTempSubdirectory().FullName;
            try
            {
                SettingsStore store = new SettingsStore(Path.Combine(folder, "settings.json"));
                File.WriteAllText(store.FilePath, "{}");
                GhosttyConfig config = GhosttyConfig.Parse("font-family = Consolas\nfont-size = 23\ncursor-style = bar\nbackground-opacity = 0.4");
                Assert.Empty(config.Diagnostics);
                AppSettings settings = store.Load();
                TerminalAppearance inherited = config.Appearance.Resolve(settings);
                Assert.Equal(23, inherited.FontSize);
                Assert.Equal("bar", inherited.CursorStyle);
                Assert.Equal(0.4, inherited.BackgroundOpacity);
                Assert.Equal(new[] { "Consolas" }, inherited.FontFamilies);
                Assert.True(store.Save(settings));
                Assert.Null(store.Load().FontSize);
                Assert.Null(store.Load().CursorStyle);
                Assert.Null(store.Load().BackgroundOpacity);
                using JsonDocument patch = JsonDocument.Parse("""{"fontFamily":"Cascadia Mono","fontSize":13,"cursorStyle":"block","backgroundOpacity":1}""");
                SettingsStore.Patch(settings, patch.RootElement);
                Assert.Null(settings.BackgroundOpacity);
                settings.BackgroundOpacity = 1;
                Assert.True(store.Save(settings));
                AppSettings saved = store.Load();
                Assert.Equal(1, saved.BackgroundOpacity);
                TerminalAppearance chosen = config.Appearance.Resolve(saved);
                Assert.Equal(13, chosen.FontSize);
                Assert.Equal("block", chosen.CursorStyle);
                Assert.Equal(0.4, chosen.BackgroundOpacity);
                Assert.Equal(new[] { "Consolas", "Cascadia Mono" }, chosen.FontFamilies);
                saved.BackgroundOpacity = 0.8;
                Assert.Equal(0, config.Appearance.Resolve(saved).BackgroundOpacity);
                Assert.True(store.Save(saved));
                Assert.Equal(0.8, store.Load().BackgroundOpacity);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void IncludesRunAfterSettingsInBreadthFirstOrderAndReloadSeesChanges()
        {
            string folder = Directory.CreateTempSubdirectory().FullName;
            try
            {
                string path = Path.Combine(folder, "ghostty.conf");
                File.WriteAllText(path, "config-file = a\nconfig-file = b\nfont-family = Consolas\nfont-size = 22\ncursor-style = bar");
                File.WriteAllText(Path.Combine(folder, "a"), "config-file = c\nfont-family = Arial\nfont-size = 20");
                File.WriteAllText(Path.Combine(folder, "b"), "font-size = 21\ncursor-style = underline\nbackground-opacity = .6");
                File.WriteAllText(Path.Combine(folder, "c"), "font-size = 24\nfont-family =\nfont-family = Courier New");
                AppSettings settings = new AppSettings { FontFamily = "Cascadia Mono", FontSize = 13, CursorStyle = "block", BackgroundOpacity = .8 };
                TerminalAppearance first = GhosttyConfig.Load(path).Appearance.Resolve(settings);
                Assert.Equal(24, first.FontSize);
                Assert.Equal("underline", first.CursorStyle);
                Assert.Equal(.6, first.BackgroundOpacity);
                Assert.Equal(new[] { "Courier New" }, first.FontFamilies);
                File.WriteAllText(Path.Combine(folder, "c"), "font-size =\ncursor-style =\nbackground-opacity =\nfont-family =");
                TerminalAppearance reload = GhosttyConfig.Load(path).Appearance.Resolve(settings);
                Assert.Equal(13, reload.FontSize);
                Assert.Equal("block", reload.CursorStyle);
                Assert.Equal(1, reload.BackgroundOpacity);
                Assert.Empty(reload.FontFamilies);
                Assert.Equal(13, settings.FontSize);
                Assert.Equal(.8, settings.BackgroundOpacity);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Theory]
        [InlineData(false, 18)]
        [InlineData(true, 20)]
        public void EmptyConfigFileResetsTheRecursiveList(bool nested, double expectedSize)
        {
            string folder = Directory.CreateTempSubdirectory().FullName;
            try
            {
                string path = Path.Combine(folder, "ghostty.conf");
                File.WriteAllText(path, "font-size = 18\nconfig-file = a\nconfig-file = b\n" + (nested ? "" : "config-file = \"\"\n"));
                File.WriteAllText(Path.Combine(folder, "a"), "font-size = 20\nconfig-file =\nconfig-file = c\n");
                File.WriteAllText(Path.Combine(folder, "b"), "font-size = 24");
                File.WriteAllText(Path.Combine(folder, "c"), "font-size = 25");
                Assert.Equal(expectedSize, GhosttyConfig.Load(path).Appearance.Resolve(new AppSettings()).FontSize);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Theory]
        [InlineData(13)]
        [InlineData(14)]
        [InlineData(22)]
        public void ExistingExplicitChoicesAreNotMigratedOrErased(double size)
        {
            string folder = Directory.CreateTempSubdirectory().FullName;
            try
            {
                SettingsStore store = new SettingsStore(Path.Combine(folder, "settings.json"));
                File.WriteAllText(store.FilePath, "{\"fontFamily\":\"Consolas\",\"fontSize\":" + size + ",\"cursorStyle\":\"block\",\"backgroundOpacity\":1}");
                AppSettings settings = store.Load();
                using JsonDocument unrelated = JsonDocument.Parse("""{"notificationsEnabled":false}""");
                SettingsStore.Patch(settings, unrelated.RootElement);
                Assert.True(store.Save(settings));
                AppSettings saved = store.Load();
                Assert.Equal("Consolas", saved.FontFamily);
                Assert.Equal(size, saved.FontSize);
                Assert.Equal("block", saved.CursorStyle);
                Assert.Equal(1, saved.BackgroundOpacity);
                Assert.Equal(size, GhosttyConfig.Parse("font-size = 25").Appearance.Resolve(saved).FontSize);
            }
            finally
            {
                Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void QuotedAppearanceValuesAndResetsUseGhosttysFileSyntax()
        {
            GhosttyConfig config = GhosttyConfig.Parse("""
                font-family = "Consolas"
                font-family = ""
                font-family = "Courier New"
                font-size = "18.5"
                cursor-style = "block_hollow"
                background-opacity = "0.25"
                """);
            Assert.Empty(config.Diagnostics);
            TerminalAppearance result = config.Appearance.Resolve(new AppSettings());
            Assert.Equal(new[] { "Courier New" }, result.FontFamilies);
            Assert.Equal(18.5, result.FontSize);
            Assert.Equal("block_hollow", result.CursorStyle);
            Assert.Equal(.25, result.BackgroundOpacity);
        }

        [Fact]
        public void InvalidAppearanceValuesAreReportedAndDoNotReplaceValidOnes()
        {
            GhosttyConfig config = GhosttyConfig.Parse("font-size = 22\nfont-size = nan\ncursor-style = bar\ncursor-style = unknown\nbackground-opacity = -1");
            Assert.Equal(3, config.Diagnostics.Count);
            TerminalAppearance result = config.Appearance.Resolve(new AppSettings());
            Assert.Equal(22, result.FontSize);
            Assert.Equal("bar", result.CursorStyle);
        }
    }
}
