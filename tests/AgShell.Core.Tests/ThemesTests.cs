using System.Text.Json;

namespace AgShell.Core.Tests
{
    public class ThemesTests
    {
        [Fact]
        public void ParsesGhosttyThemesLeniently()
        {
            TerminalTheme? theme = Themes.Parse("Test", "palette = 0=#21222C\npalette = 15=ffffff\npalette = 99=#123456\nbackground= #282a36\nforeground = #f8f8f2\ncursor-color = nope\n# comment\nselection-background = #44475a");

            Assert.NotNull(theme);
            Assert.Equal("#21222c", theme.Palette[0]);
            Assert.Equal("#ffffff", theme.Palette[15]);
            Assert.Null(theme.Palette[1]);
            Assert.Equal("#282a36", theme.Background);
            Assert.Null(theme.Cursor);
            Assert.Equal("#44475a", theme.SelectionBackground);
            Assert.True(theme.IsDark);
            Assert.Equal(Themes.GhosttyBackground, Themes.Parse("Empty", "foreground = #ffffff")!.Background);
        }

        [Fact]
        public void BrightnessUsesPerceivedLuminance()
        {
            // agterm's ThemeBrightnessTests.
            Assert.True(Themes.IsDark("#000000"));
            Assert.False(Themes.IsDark("#ffffff"));
            Assert.True(Themes.IsDark("#1d2021"));
            Assert.True(Themes.IsDark("#002b36"));
            Assert.False(Themes.IsDark("#fdf6e3"));
            Assert.False(Themes.IsDark("#00ff00"));
            Assert.True(Themes.IsDark("#0000ff"));
            Assert.True(Themes.IsDark("#ff0000"));
            Assert.False(Themes.IsDark("#8c8c8c"));
            Assert.True(Themes.IsDark("#737373"));
        }

        [Fact]
        public void SidebarBrightnessWashesTheTintOverTheBackground()
        {
            Assert.True(Themes.IsDark("#1d2021", 0));
            Assert.False(Themes.IsDark("#fdf6e3", 0));
            Assert.True(Themes.IsDark("#ea3323"));
            Assert.False(Themes.IsDark("#ea3323", -0.30));
            Assert.False(Themes.IsDark("#8c8c8c"));
            Assert.True(Themes.IsDark("#8c8c8c", 0.30));
            Assert.Equal(-0.30, new AppSettings { SidebarBackgroundShift = 0 }.SidebarShiftAmount, 10);
            Assert.Equal(0, new AppSettings().SidebarShiftAmount);
            Assert.Equal(0.30, new AppSettings { SidebarBackgroundShift = 10 }.SidebarShiftAmount, 10);
        }

        [Fact]
        public void AppsAreLightUnlessWindowsSaysDark()
        {
            Assert.True(Themes.AppsDark(0));
            Assert.False(Themes.AppsDark(1));
            Assert.False(Themes.AppsDark(null));
            Assert.False(Themes.AppsDark("0"));
        }

        [Fact]
        public void ColorsReadAsGhosttyReadsThem()
        {
            // X11 names from Ghostty's rgb.txt, not the CSS ones .NET knows.
            Assert.Equal("#00ff00", Themes.GhosttyColor("green"));
            Assert.Equal("#bebebe", Themes.GhosttyColor("gray"));
            Assert.Equal("#b03060", Themes.GhosttyColor("maroon"));
            Assert.Equal("#a020f0", Themes.GhosttyColor("purple"));
            Assert.Equal("#228b22", Themes.GhosttyColor("FoReStGReen"));
            Assert.Equal("#00fa9a", Themes.GhosttyColor("medium spring green"));
            Assert.Equal("#f8f8ff", Themes.GhosttyColor(" \tghost white "));
            Assert.Null(Themes.GhosttyColor("nosuchcolor"));
            Assert.Null(Themes.GhosttyColor("mediumspring green"));

            Assert.Equal("#ffffff", Themes.GhosttyColor("#fff"));
            Assert.Equal("#112233", Themes.GhosttyColor("#112233"));
            Assert.Equal("#ff8000", Themes.GhosttyColor("#fff808000"));
            Assert.Equal("#ff8000", Themes.GhosttyColor("#ffff80800000"));
            Assert.Equal("#aabbcc", Themes.GhosttyColor("AABBCC"));
            Assert.Equal("#aabbcc", Themes.GhosttyColor("abc"));
            Assert.Equal("#ff8000", Themes.GhosttyColor("rgb:f/80/000"));
            Assert.Equal("#ff7f00", Themes.GhosttyColor("rgbi:1/0.5/0"));
            Assert.Null(Themes.GhosttyColor("rgbi:1/1.5/0"));
            Assert.Null(Themes.GhosttyColor("#12345"));
            Assert.Null(Themes.GhosttyColor("12345"));
            Assert.Null(Themes.GhosttyColor(""));
        }

        [Fact]
        public void TheDarkSlotAppliesOnlyWhenFollowingADarkSystem()
        {
            AppSettings settings = new AppSettings { Theme = "Light One", DarkTheme = "Dark One" };
            Assert.Equal("Light One", Themes.ActiveName(settings, systemDark: true));

            settings.FollowSystemAppearance = true;
            Assert.Equal("Dark One", Themes.ActiveName(settings, systemDark: true));
            Assert.Equal("Light One", Themes.ActiveName(settings, systemDark: false));
        }

        [Fact]
        public void NoThemeIsGhosttysOwnAndALoneSideIsUsedAsTheSingleTheme()
        {
            Assert.Null(Themes.ActiveName(new AppSettings(), systemDark: true));
            Assert.Null(Themes.ActiveName(new AppSettings { Theme = " " }, systemDark: false));
            Assert.Equal("Dark One", Themes.ActiveName(new AppSettings { DarkTheme = "Dark One", FollowSystemAppearance = true }, systemDark: false));
            Assert.Equal(Themes.GhosttyBackground, Themes.BuiltIn.Background);
            Assert.Equal(Themes.GhosttyForeground, Themes.BuiltIn.Foreground);
        }

        [Fact]
        public void AFreshInstallStartsWithAgshellsThemeAndASavedFileWithoutOneKeepsNone()
        {
            string path = Path.Combine(Path.GetTempPath(), "agshell-settings-" + Guid.NewGuid() + ".json");
            try
            {
                SettingsStore store = new SettingsStore(path);
                Assert.Equal(Themes.DefaultName, store.Load().Theme);

                File.WriteAllText(path, "{ \"fontSize\": 14 }");
                Assert.Null(store.Load().Theme);

                File.WriteAllText(path, "{ broken");
                Assert.Equal(Themes.DefaultName, store.Load().Theme);
            }
            finally
            {
                foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + "*"))
                {
                    File.Delete(file);
                }
            }
        }

        [Fact]
        public void CatalogPrefersUserThemesAndRejectsPaths()
        {
            string root = Path.Combine(Path.GetTempPath(), "agshell-themes-" + Guid.NewGuid());
            string user = Path.Combine(root, "user");
            string bundled = Path.Combine(root, "bundled");
            Directory.CreateDirectory(user);
            Directory.CreateDirectory(bundled);
            try
            {
                File.WriteAllText(Path.Combine(bundled, "Same"), "background = #000000\nforeground = #ffffff");
                File.WriteAllText(Path.Combine(bundled, "beta"), "background = #000000\nforeground = #ffffff");
                File.WriteAllText(Path.Combine(user, "Same"), "background = #ffffff\nforeground = #000000");
                ThemeCatalog catalog = new ThemeCatalog(user, bundled);

                File.WriteAllText(Path.Combine(bundled, "_under"), "background = #000000\nforeground = #ffffff");
                File.WriteAllText(Path.Combine(bundled, "Ålesund"), "background = #000000\nforeground = #ffffff");
                ThemeCatalog sorted = new ThemeCatalog(user, bundled);

                // agterm's localizedCaseInsensitiveCompare, and an exact name to contain.
                Assert.Equal(new[] { "_under", "Ålesund", "beta", "Same" }, sorted.Names());
                Assert.True(catalog.Contains("Same"));
                Assert.False(catalog.Contains("same"));
                Assert.False(catalog.Contains("Same\n"));
                Assert.Equal("#ffffff", catalog.Load("same")!.Background);
                Assert.Null(catalog.Load("..\\bundled\\beta"));
                Assert.Null(catalog.Load("missing"));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void SettingsPatchSetsClearsAndReportsUnknownFields()
        {
            AppSettings settings = new AppSettings { Theme = "Old", ConfirmCloseSession = true };
            using JsonDocument patch = JsonDocument.Parse("""{"theme":"Dracula","confirmCloseSession":null,"sidebarBackgroundShift":7.4,"activeStatusColorHex":"#ABCDEF","blockedStatusColorHex":"red","bogus":1}""");

            IReadOnlyList<string> unknown = SettingsStore.Patch(settings, patch.RootElement);

            Assert.Equal("Dracula", settings.Theme);
            Assert.Null(settings.ConfirmCloseSession);
            Assert.Equal(7, settings.SidebarBackgroundShift);
            Assert.Equal("#abcdef", settings.ActiveStatusColorHex);
            Assert.Null(settings.BlockedStatusColorHex);
            Assert.Equal(new[] { "bogus" }, unknown);
        }

        [Fact]
        public void UnknownEnumValuesAndOutOfRangeNumbersReadAsDefaults()
        {
            AppSettings settings = new AppSettings { CursorStyle = "block_hollow", StatusReset = "sometimes", SidebarBackgroundShift = 40, FontSize = 200 };

            Assert.Equal("block", settings.EffectiveCursorStyle);
            Assert.Equal("firstKey", settings.EffectiveStatusReset);
            Assert.Equal(10, settings.EffectiveSidebarBackgroundShift);
            Assert.Equal(200, settings.EffectiveFontSize);
            Assert.Equal("off", settings.EffectiveTaskbarFlash);
        }

        [Fact]
        public void SettingsRoundTripOmittingDefaults()
        {
            string path = Path.Combine(Path.GetTempPath(), "agshell-settings-" + Guid.NewGuid() + ".json");
            try
            {
                SettingsStore store = new SettingsStore(path);
                store.Save(new AppSettings { Theme = "Тема", FollowSystemAppearance = true });

                string text = File.ReadAllText(path);
                Assert.Contains("\"theme\": \"Тема\"", text);
                Assert.DoesNotContain("darkTheme", text);
                Assert.True(store.Load().FollowSystemAppearance);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void ThemesAreReadAsLibghosttyReadsThem()
        {
            TerminalTheme theme = Themes.Parse("t", "background = \"#282828\"\nforeground = abc\ncursor-color = Dark Orange\nselection-background = #112233\nselection-background =\npalette = 1=#f00\npalette = 1=nonsense\n")!;

            Assert.Equal("#282828", theme.Background);
            Assert.Equal("#aabbcc", theme.Foreground);
            Assert.Equal("#ff8c00", theme.Cursor);
            Assert.Null(theme.SelectionBackground);
            Assert.Equal("#ff0000", theme.Palette[1]);
        }

        [Fact]
        public void AThemeWithoutColorsTakesGhosttysDefaults()
        {
            TerminalTheme theme = Themes.Parse("t", "palette = 0=#000000\n")!;

            Assert.Equal(Themes.GhosttyBackground, theme.Background);
            Assert.Equal(Themes.GhosttyForeground, theme.Foreground);
        }
    }
}
