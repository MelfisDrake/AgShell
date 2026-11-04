namespace AgShell.Core.Tests
{
    public class PageThemeTests
    {
        private static TerminalTheme Theme(string background, string foreground, IReadOnlyList<string?>? palette = null)
        {
            return new TerminalTheme("T", background, foreground, null, null, null, null, palette ?? new string?[16]);
        }

        [Fact]
        public void ALocalPageWearsTheThemeAndEveryPageGetsTheVariables()
        {
            string?[] palette = new string?[16];
            palette[1] = "#ff0000";
            palette[2] = "nope";
            PageTheme theme = PageTheme.Of(Theme("#1b1a18", "#e6e1d7", palette), null);

            Assert.Equal(":where(html) { color-scheme: dark; color: #e6e1d7; --agshell-background: #1b1a18; --agshell-foreground: #e6e1d7; --agshell-color-1: #ff0000; }", theme.Stylesheet(themed: true));
            Assert.Equal(":where(html) { --agshell-background: #1b1a18; --agshell-foreground: #e6e1d7; --agshell-color-1: #ff0000; }", theme.Stylesheet(themed: false));
        }

        [Fact]
        public void TheOverlaysBackgroundReplacesTheThemesAndSetsTheScheme()
        {
            PageTheme theme = PageTheme.Of(Theme("#1b1a18", "#e6e1d7"), "#fdf6e3");

            Assert.Equal("#fdf6e3", theme.Background);
            Assert.False(theme.Dark);
            Assert.Equal("#1b1a18", PageTheme.Of(Theme("#1b1a18", "#e6e1d7"), "red").Background);
        }

        [Fact]
        public void ColorsThatAreNotHexFallBackToAPlainPair()
        {
            PageTheme theme = PageTheme.Of(Theme("#000000", "white", new string?[3]), null);

            Assert.Equal("#1e1e1e", theme.Background);
            Assert.Equal("#d4d4d4", theme.Foreground);
            Assert.Empty(theme.Palette);
        }
    }
}
