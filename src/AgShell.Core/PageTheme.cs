namespace AgShell.Core
{
    /// <summary>
    /// agterm's HtmlOverlayTheme: the terminal theme as a page's default style. Every page gets the colors as variables; a
    /// local page also wears the theme's text color and light or dark scheme. The rule has no weight, so any style the page
    /// sets itself wins.
    /// </summary>
    public sealed record PageTheme(string Background, string Foreground, bool Dark, IReadOnlyList<string> Palette)
    {
        /// <summary>The theme in force, with an overlay's --background-color in place of its background.</summary>
        public static PageTheme Of(TerminalTheme theme, string? backgroundColor)
        {
            string background = backgroundColor is not null && IsHex(backgroundColor) ? backgroundColor : theme.Background;
            bool valid = IsHex(background) && IsHex(theme.Foreground);
            bool dark = valid ? Themes.IsDark(background) : theme.IsDark;
            // A slot that is not a color stays empty, so the others keep their place.
            IReadOnlyList<string> palette = theme.Palette.Count == 16 ? theme.Palette.Select(color => color is not null && IsHex(color) ? color : string.Empty).ToList() : Array.Empty<string>();
            return new PageTheme(
                valid ? background : (dark ? "#1e1e1e" : "#ffffff"),
                valid ? theme.Foreground : (dark ? "#d4d4d4" : "#1e1e1e"),
                dark,
                palette);
        }

        public string Stylesheet(bool themed)
        {
            string look = themed ? "color-scheme: " + (Dark ? "dark" : "light") + "; color: " + Foreground + "; " : string.Empty;
            string slots = string.Concat(Palette.Select((color, slot) => color.Length == 0 ? string.Empty : "--agshell-color-" + slot + ": " + color + "; "));
            return ":where(html) { " + look + "--agshell-background: " + Background + "; --agshell-foreground: " + Foreground + "; " + slots + "}";
        }

        private static bool IsHex(string color)
        {
            return color.Length == 7 && color[0] == '#' && color.Skip(1).All(Uri.IsHexDigit);
        }
    }
}
