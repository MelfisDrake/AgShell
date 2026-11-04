using AgShell.Core;

namespace AgShell.Core.Tests
{
    public class PastedPathTests
    {
        [Theory]
        [InlineData(@"C:\work\image.png", @"C:\work\image.png")]
        [InlineData(@"C:\my files\a.txt", @"C:\my` files\a.txt")]
        [InlineData(@"C:\cost$x\a`b.txt", @"C:\cost`$x\a``b.txt")]
        [InlineData(@"C:\it's\{x}.txt", @"C:\it`'s\`{x`}.txt")]
        [InlineData("C:\\a\u2019b", "C:\\a`\u2019b")]
        [InlineData(@"C:\ps test(1)\s.ps1", @"C:\ps` test`(1`)\s.ps1")]
        public void PowerShellBacktickEscapesOnlyWhatIsSignificant(string path, string expected)
        {
            Assert.Equal(expected, PastedPath.ForPowerShell(path));
        }

        [Theory]
        [InlineData(@"C:\work\image.png", @"C:\work\image.png")]
        [InlineData(@"C:\my files\a.txt", "\"C:\\my files\\a.txt\"")]
        [InlineData(@"C:\100%\a.txt", "\"C:\\100%\\a.txt\"")]
        [InlineData(@"C:\a=b^c", "\"C:\\a=b^c\"")]
        [InlineData(@"C:\cost$x", @"C:\cost$x")]
        public void CmdDoubleQuotesOnTheCharactersCmdNames(string path, string expected)
        {
            Assert.Equal(expected, PastedPath.ForCmd(path));
        }

        [Theory]
        [InlineData("/mnt/c/work/image.png", "/mnt/c/work/image.png")]
        [InlineData("/mnt/c/my files/a (1).txt", @"/mnt/c/my\ files/a\ \(1\).txt")]
        [InlineData("/mnt/c/it's", @"/mnt/c/it\'s")]
        [InlineData(@"a\b", @"a\\b")]
        public void PosixEscapesLikeAgtermShellEscape(string path, string expected)
        {
            Assert.Equal(expected, PastedPath.ForPosix(path));
        }

        [Theory]
        [InlineData(@"C:\Work\a.txt", "/mnt/c/Work/a.txt")]
        [InlineData(@"\\wsl.localhost\Ubuntu\home\me", "/home/me")]
        [InlineData(@"\\wsl$\Ubuntu\tmp", "/tmp")]
        public void TranslatesWindowsPathsForWsl(string path, string expected)
        {
            Assert.Equal(expected, PastedPath.WslPath(path));
        }
    }
}
