using AgShell.Core;

namespace AgShell.Core.Tests
{
    public class CommandPathTests
    {
        private static string? Lookup(string name)
        {
            return name.ToUpperInvariant() switch
            {
                "SYSTEMROOT" => @"C:\Windows",
                "LOCALAPPDATA" => @"C:\Users\me\AppData\Local",
                _ => null,
            };
        }

        [Fact]
        public void PutsTheBundledFolderFirstThenRegistryThenProcessWithoutDuplicates()
        {
            string path = CommandPath.Widened(
                @"D:\agshell\",
                @"%SystemRoot%\system32;%SystemRoot%;C:\Program Files\Git\cmd",
                @"%LOCALAPPDATA%\Microsoft\WindowsApps;D:\agshell;C:\new\tool\",
                @"D:\agshell;C:\Windows\System32;C:\dev\only",
                Lookup);

            Assert.Equal(@"D:\agshell;C:\Windows\system32;C:\Windows;C:\Program Files\Git\cmd;C:\Users\me\AppData\Local\Microsoft\WindowsApps;C:\new\tool\;C:\dev\only", path);
        }

        [Fact]
        public void LeavesUnknownVariablesAsWritten()
        {
            Assert.Equal(@"%NOPE%\bin;C:\Windows\x%", CommandPath.Expand(@"%NOPE%\bin;%SystemRoot%\x%", Lookup));
        }

        [Fact]
        public void WorksWithoutRegistryValues()
        {
            Assert.Equal(@"D:\agshell;C:\a", CommandPath.Widened(@"D:\agshell", null, null, @"C:\a;;D:\agshell\", Lookup));
        }
    }
}
