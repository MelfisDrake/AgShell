using AgShell.Core;

namespace AgShell.Core.Tests
{
    public class UserPathInstallTests
    {
        private static string Expand(string entry)
        {
            return entry.Replace("%LOCALAPPDATA%", @"C:\Users\me\AppData\Local");
        }

        [Fact]
        public void AddsTheFolderAndDropsOtherFoldersWithAgShellctl()
        {
            HashSet<string> holding = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Users\me\AppData\Local\Programs\AgShell", @"D:\old\agshell" };

            UserPathChange change = UserPathInstall.Apply(@"C:\tools;%LOCALAPPDATA%\Programs\AgShell;D:\old\agshell\;\\server\gone", @"D:\new\agshell\", Expand, folder => holding.Contains(folder.TrimEnd('\\')));

            Assert.Equal(@"C:\tools;\\server\gone;D:\new\agshell", change.Path);
            Assert.Equal(new[] { @"%LOCALAPPDATA%\Programs\AgShell", @"D:\old\agshell\" }, change.Removed);
            Assert.False(change.AlreadyPresent);
        }

        [Fact]
        public void KeepsItsOwnEntryInPlaceOnce()
        {
            UserPathChange change = UserPathInstall.Apply(@"D:\New\AgShell;C:\tools;D:\new\agshell\", @"D:\new\agshell\", Expand, folder => folder.StartsWith(@"D:\", StringComparison.OrdinalIgnoreCase));

            Assert.Equal(@"D:\New\AgShell;C:\tools", change.Path);
            Assert.Empty(change.Removed);
            Assert.True(change.AlreadyPresent);
        }

        [Fact]
        public void AnEmptyPathGetsTheFolder()
        {
            Assert.Equal(@"D:\agshell", UserPathInstall.Apply(string.Empty, @"D:\agshell", Expand, folder => false).Path);
        }

        [Fact]
        public void TheSystemPathLosesOtherFoldersButNeverGetsThisOne()
        {
            HashSet<string> holding = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Program Files\AgShell", @"D:\new\agshell" };

            UserPathChange change = UserPathInstall.Without(@"C:\Windows;C:\Program Files\AgShell;;D:\new\agshell", @"D:\new\agshell", Expand, folder => holding.Contains(folder));
            Assert.Equal(@"C:\Windows;D:\new\agshell", change.Path);
            Assert.Equal(new[] { @"C:\Program Files\AgShell" }, change.Removed);

            UserPathChange none = UserPathInstall.Without(@"C:\Windows;;C:\bin", @"D:\new\agshell", Expand, folder => false);
            Assert.Equal((@"C:\Windows;;C:\bin", 0), (none.Path, none.Removed.Count));
        }
    }
}
