namespace AgShell.Core.Tests
{
    public class ThemeNameTests
    {
        [Fact]
        public void APlainNameIsBothSidesTrimmed()
        {
            Assert.Equal(new ThemePair("Gruvbox Dark Hard", "Gruvbox Dark Hard"), ThemeName.Parse("Gruvbox Dark Hard"));
            Assert.Equal(new ThemePair("Alabaster", "Alabaster"), ThemeName.Parse("  Alabaster\t"));
            Assert.Null(ThemeName.Parse(""));
        }

        [Fact]
        public void ALightDarkPairPicksTheSideInAnyOrder()
        {
            ThemePair pair = ThemeName.Parse("light:Alabaster,dark:Gruvbox Dark Hard")!.Value;
            Assert.Equal("Alabaster", pair.Side(dark: false));
            Assert.Equal("Gruvbox Dark Hard", pair.Side(dark: true));
            Assert.Equal(pair, ThemeName.Parse("dark:Gruvbox Dark Hard,light:Alabaster"));
            Assert.Equal(new ThemePair("GitHub Light Default", "Gruvbox Dark Hard"), ThemeName.Parse("light: GitHub Light Default , dark: Gruvbox Dark Hard "));
            Assert.Equal(new ThemePair("~/themes/my day", "Gruvbox, Dark"), ThemeName.Parse("light:\"~/themes/my day\",dark:\"Gruvbox, Dark\""));
            Assert.Equal(new ThemePair("B", "C"), ThemeName.Parse("light:A,dark:C,light:B"));
            // Ghostty takes an empty side; that side then fails to load.
            Assert.Equal(new ThemePair("", "Gruvbox Dark Hard"), ThemeName.Parse("light:,dark:Gruvbox Dark Hard"));
        }

        [Fact]
        public void AMalformedPairIsAnInvalidValueAsGhosttyReportsIt()
        {
            Assert.Null(ThemeName.Parse("light:Alabaster"));
            Assert.Null(ThemeName.Parse("dark:Gruvbox Dark Hard"));
            Assert.Null(ThemeName.Parse("LIGHT:Alabaster,DARK:Gruvbox Dark Hard"));
            Assert.Null(ThemeName.Parse("light=Alabaster,dark=Gruvbox"));
            Assert.Null(ThemeName.Parse("light:\"open,dark:x"));
        }

        [Fact]
        public void ADriveLetterIsAPathNotAPair()
        {
            Assert.Equal(new ThemePair(@"C:\themes\day", @"C:\themes\day"), ThemeName.Parse(@"C:\themes\day"));
            Assert.Equal(new ThemePair(@"C:\themes\day", @"D:\night"), ThemeName.Parse(@"light:C:\themes\day,dark:D:\night"));
        }

        [Fact]
        public void LookupFollowsGhosttysThemeOpen()
        {
            string root = Path.Combine(Path.GetTempPath(), "agshell-theme-name-" + Guid.NewGuid());
            string user = Path.Combine(root, "user");
            string bundled = Path.Combine(root, "bundled");
            string home = Path.Combine(root, "home");
            Directory.CreateDirectory(user);
            Directory.CreateDirectory(bundled);
            Directory.CreateDirectory(Path.Combine(home, "themes"));
            try
            {
                File.WriteAllText(Path.Combine(bundled, "Both"), "background = #000000");
                File.WriteAllText(Path.Combine(user, "Both"), "background = #ffffff");
                File.WriteAllText(Path.Combine(bundled, "Bundled"), "background = #111111");
                File.WriteAllText(Path.Combine(home, "themes", "day"), "background = #eeeeee");

                Assert.Equal("#ffffff", ThemeName.Find("Both", user, bundled, home).Theme!.Background);
                Assert.Equal("#111111", ThemeName.Find("Bundled", user, bundled, home).Theme!.Background);
                Assert.Null(ThemeName.Find("Bundled", user, null, home).Theme);
                Assert.Equal("#eeeeee", ThemeName.Find(Path.Combine(home, "themes", "day"), user, bundled, home).Theme!.Background);
                Assert.Equal("#eeeeee", ThemeName.Find("~/themes/day", user, bundled, home).Theme!.Background);
                Assert.Equal("#eeeeee", ThemeName.Find(@"~\themes\day", user, bundled, home).Theme!.Background);

                (TerminalTheme? missing, IReadOnlyList<string> tried) = ThemeName.Find("Gone", user, bundled, home);
                Assert.Null(missing);
                Assert.Equal(new[] { $"theme \"Gone\" not found, tried path \"{Path.Combine(user, "Gone")}\"", $"theme \"Gone\" not found, tried path \"{Path.Combine(bundled, "Gone")}\"" }, tried);

                foreach (string relative in new[] { "themes/day", @".\day", @"..\bundled\Both" })
                {
                    Assert.Equal(new[] { $"theme \"{relative}\" cannot include path separators unless it is an absolute path" }, ThemeName.Find(relative, user, bundled, home).Diagnostics);
                }

                foreach (string untouched in new[] { "~", "~me/themes/x" })
                {
                    Assert.Null(ThemeName.Find(untouched, user, bundled, home).Theme);
                }

                Assert.Equal(new[] { $"failed to load theme \"\" from the file \"{user}\"" }, ThemeName.Find("", user, bundled, home).Diagnostics);

                string absent = Path.Combine(home, "nope");
                Assert.Equal(new[] { $"failed to load theme from the path \"{absent}\"" }, ThemeName.Find(absent, user, bundled, home).Diagnostics);
                Assert.Equal(new[] { $"not reading theme from \"{home}\": it is a directory" }, ThemeName.Find(home, user, bundled, home).Diagnostics);
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Fact]
        public void TheSettingsThemeWinsOverGhosttyConf()
        {
            ThemePair conf = new ThemePair("Day", "Night");
            Assert.Equal("Night", Themes.ActiveName(new AppSettings(), systemDark: true, conf));
            Assert.Equal("Day", Themes.ActiveName(new AppSettings(), systemDark: false, conf));
            Assert.Equal("Mine", Themes.ActiveName(new AppSettings { Theme = "Mine" }, systemDark: true, conf));
            Assert.Null(Themes.ActiveName(new AppSettings(), systemDark: true, null));
        }

        [Fact]
        public void GhosttyConfReadsTheThemeKey()
        {
            Assert.Equal(new ThemePair("Day", "Night"), GhosttyConfig.Parse("theme = light:Day,dark:Night").Theme);
            Assert.Equal(new ThemePair("Dracula", "Dracula"), GhosttyConfig.Parse("theme = \"Dracula\"").Theme);
            Assert.Null(GhosttyConfig.Parse("theme = Dracula\ntheme =").Theme);
            GhosttyConfig invalid = GhosttyConfig.Parse("theme = light:Day");
            Assert.Null(invalid.Theme);
            Assert.Equal(new[] { "ghostty.conf:1: invalid value for theme: light:Day" }, invalid.Diagnostics);
        }
    }
}
