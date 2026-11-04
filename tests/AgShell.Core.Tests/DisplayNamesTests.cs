namespace AgShell.Core.Tests
{
    public class DisplayNamesTests
    {
        [Theory]
        [InlineData(@"C:\src\agshell", "agshell")]
        [InlineData(@"C:\src\agshell\", "agshell")]
        [InlineData(@"C:\", @"C:\")]
        [InlineData("C:", "C:")]
        [InlineData("/home/developer", "developer")]
        [InlineData("/home/developer/", "developer")]
        [InlineData("/", "/")]
        [InlineData("", "~")]
        [InlineData(null, "~")]
        public void BasenameOfPath(string? path, string expected)
        {
            Assert.Equal(expected, DisplayNames.Basename(path));
        }

        [Fact]
        public void CustomNameWinsOverPath()
        {
            Assert.Equal("agent", DisplayNames.ForSession("  agent ", "✳ Fix the icon", @"C:\src"));
        }

        [Fact]
        public void TitleWinsOverPath()
        {
            Assert.Equal("✳ Fix the icon", DisplayNames.ForSession(null, " ✳ Fix the icon ", @"C:\src"));
            Assert.Equal("src", DisplayNames.ForSession(null, "  ", @"C:\src"));
        }

        [Theory]
        [InlineData("drake@DESKTOP-1: ~/src")]
        [InlineData("(chroot)drake@host: /tmp")]
        [InlineData(@"C:\WINDOWS\system32\cmd.exe")]
        [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe")]
        [InlineData(@"C:\WINDOWS\system32\cmd.exe - dir")]
        [InlineData("/home/developer/src")]
        [InlineData(" \u0007 ")]
        [InlineData("✳")]
        public void ShellTitlesAreDropped(string raw)
        {
            Assert.Null(DisplayNames.TerminalTitle(raw, "/home/developer/src"));
        }

        [Theory]
        [InlineData("✳ Fix the icon", "Fix the icon")]
        [InlineData("\u2810 Claude Code", "Claude Code")]
        [InlineData("Fix ✳ icon", "Fix ✳ icon")]
        [InlineData("vim notes.md", "vim notes.md")]
        [InlineData("a\nb\u001b", "ab")]
        [InlineData("mail@example.com", "mail@example.com")]
        public void ProgramTitlesAreKept(string raw, string expected)
        {
            Assert.Equal(expected, DisplayNames.TerminalTitle(raw, "/home/developer/src"));
        }
    }
}
